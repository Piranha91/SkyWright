"""The report compiler against the oracle (Q45).

    python _geometry-harness/tests/test_evalreport.py            # from the checkout root
    python _geometry-harness/tests/test_evalreport.py R4         # one case (a prefix match); `fidelity` too

Dependency-free; exits 1 on failure. The fixtures are REAL schema-2 bundles made at test time by driving
`evalrun.py` over `test_evalrun.py`'s engine (`fixtures/evalreport/make_report_fixtures.py`); the compiler
under test is `_geometry-harness/evalreport.py` (`plan` / `report` / `verify` / `narrate-apply`). What is
asserted comes from `fixtures/evalreport/expected/*.json`, written from PLAN.md's D1-D7 and the packet's
acceptance table BEFORE the compiler existed. Where the code and the oracle disagree the oracle is not edited
to match: the test fails and the disagreement is reported with both values and the rule.

conflictdiff is built from this checkout's source once per run (HOUSECARL_EVALRUN_TEST_HARNESS names a tree to
use as given instead). A missing compiler, a conflictdiff that does not build or a missing browser is a FAILURE
that names the file - never a skip.
"""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import types
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
import reporting_data  # noqa: E402  the module's own reporting data
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
import os as _os_settings, tempfile as _tf_settings  # noqa: E402
# The evaluation tools read the saved evaluation settings. These known answers build their bundles with evalrun and
# judge the load-time state, so they run on a private Standard-depth file, never the user's own (Deep is covered by
# test_stategen.py).
_settings_file = Path(_tf_settings.mkdtemp(prefix="hc-evalreport-settings-")) / "houseCARL.evaluation.json"
_settings_file.write_text('{"schemaVersion": 1, "evaluation": {"depth": "standard"}}', encoding="utf-8")
_os_settings.environ["HOUSECARL_EVAL_SETTINGS"] = str(_settings_file)
HARNESS = HERE.parent
FIXT = SW.HARNESS / "tests" / "fixtures" / "evalreport"
EXPECTED = FIXT / "expected"
FIDELITY = FIXT / "fidelity"
# HOUSECARL_EVALREPORT names another tree's compiler to test (an integration head); the default is this
# checkout's own. HOUSECARL_EVALRUN does the same for the controller the fixtures are built with.
EVALREPORT = Path(os.environ.get("HOUSECARL_EVALREPORT") or (HARNESS / "evalreport.py"))
# the stylesheet the compiler under test inlines - beside that compiler, so an integration head is read from
# its own tree rather than this checkout's
REPORT_CSS = EVALREPORT.parent / "evalreport_templates" / "report.css"
sys.path.insert(0, str(FIXT))

FAILURES: list[str] = []
DISAGREEMENTS: list[str] = []
FORBIDDEN = re.compile(r"\b(pass|passes|passed|compatible|clean)\b", re.IGNORECASE)
TOKEN = re.compile(r"\[(E-[0-9a-f]{16}|M-[0-9a-f]{12})\]")
IMG_MD = re.compile(r"!\[[^\]]*\]\(([^)\s]+)")
IMG_HTML = re.compile(r"<img[^>]+src=\"([^\"]+)\"")


def check(condition, label):
    print(("  PASS  " if condition else "  FAIL  ") + label)
    if not condition:
        FAILURES.append(label)


def expect(got, want, label, rule=""):
    ok = got == want
    print(("  PASS  " if ok else "  FAIL  ") + f"{label}: expected {want!r}, got {got!r}")
    if not ok:
        FAILURES.append(label)
        DISAGREEMENTS.append(f"{label}: oracle {want!r} vs code {got!r}{('  [' + rule + ']') if rule else ''}")


def load(p: Path):
    return json.loads(Path(p).read_text(encoding="utf-8")) if Path(p).exists() else None


def text(p: Path) -> str:
    return Path(p).read_text(encoding="utf-8", errors="replace") if Path(p).exists() else ""


def sha256_file(p: Path) -> str:
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def evalreport(args, timeout=600):
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    return subprocess.run([sys.executable, str(EVALREPORT)] + [str(a) for a in args],
                          capture_output=True, text=True, env=env, timeout=timeout)


def cli_present(case: str) -> bool:
    ok = EVALREPORT.exists()
    check(ok, f"{case}: the compiler exists at {EVALREPORT} (a missing CLI is a failure, not a skip)")
    return ok


def scrub(res, d: Path) -> str:
    out = (res.stdout or "") + (res.stderr or "")
    for form in (str(d), str(d).replace("\\", "/"), d.name):
        out = out.replace(form, "<DIR>").replace(form.replace("\\", "\\\\"), "<DIR>")
    return out


MD_ESCAPE = re.compile(r"\\([\\`*_{}\[\]()#+\-.!|>~\"'])")


def normalise(s: str) -> str:
    """Markdown backslash escapes removed and whitespace collapsed, so a quoted sentence matches its
    report.md rendering (Fable, 20:12: the plan-text scan passed where the report.md scan flagged the same
    sentence, because the renderer escapes and wraps it)."""
    return re.sub(r"\s+", " ", MD_ESCAPE.sub(r"\1", s or "")).strip()


def forbidden_hits(s: str, exempt) -> list[str]:
    s = normalise(s)
    for tok in sorted({normalise(t) for t in exempt or [] if t}, key=len, reverse=True):
        if not tok:
            continue
        s = s.replace(tok, " ")
        if len(tok) > 60:
            # a truncated rendering (a `short()` cut): match on the sentence's first 60 characters and drop
            # however much of the exemption follows them
            head = tok[:60]
            i = s.find(head)
            while i != -1:
                n = 60
                while n < len(tok) and i + n < len(s) and s[i + n] == tok[n]:
                    n += 1
                s = s[:i] + " " + s[i + n:]
                i = s.find(head)
    return [m.group(0) for m in FORBIDDEN.finditer(s)]


def string_leaves(obj) -> list[str]:
    if isinstance(obj, str):
        return [obj]
    if isinstance(obj, dict):
        return [s for v in obj.values() for s in string_leaves(v)]
    if isinstance(obj, list):
        return [s for v in obj for s in string_leaves(v)]
    return []


def registry_exemptions(bundle: Path) -> list[str]:
    """Rulings A-4 (Fable, 19:20) and A-12 (19:53): the forbidden-word rule binds the COMPILER'S OWN sentences,
    not strings it quotes verbatim. Exempt, exactly as PASSES-COMPLETED-CHECKS is: every check's `title`,
    `evidence.claim` and `evidence.limits` from the bundle's registry copy; every string leaf under the
    disposition's `unassessed`; and the scope copy's `unmeasured[].why` - whichever bundle is scanned."""
    man = load(bundle / "manifest.json") or {}
    reg = load(bundle / str(man.get("registry") or "registry/check-registry.json")) or {}
    out = []
    for c in reg.get("checks") or []:
        ev = c.get("evidence") or {}
        for v in (c.get("title"), ev.get("claim"), ev.get("limits")):
            if isinstance(v, str):
                out.append(v)
            elif isinstance(v, list):
                out += [x for x in v if isinstance(x, str)]
    disp = load(bundle / "disposition.json") or {}
    out += string_leaves(disp.get("unassessed"))
    scope = load(bundle / str(man.get("scope") or "scope/scope.json")) or {}
    for u in scope.get("unmeasured") or []:
        if isinstance(u, dict) and isinstance(u.get("why"), str):
            out.append(u["why"])
        elif isinstance(u, str):
            out.append(u)
    return [s for s in out if s and s.strip()]


NET = re.compile(r"\bnet(?:s|ted|ting)?\b", re.IGNORECASE)


def netting_claims(s: str) -> list[str]:
    """Affirmative netting claims only (Fable, 19:53): a `net` word NOT preceded within three words by
    `not` or `never`. "…which do not net against the new defect" is the packet's own wording and passes."""
    out = []
    for m in NET.finditer(s):
        before = re.findall(r"[A-Za-z']+", s[max(0, m.start() - 80):m.start()])[-3:]
        if any(w.lower() in ("not", "never") for w in before):
            continue
        out.append(s[max(0, m.start() - 25):m.end() + 25].replace("\n", " ").strip())
    return out


def has_anchor(index_text: str, ident: str) -> bool:
    if f'id="{ident}"' in index_text or f'name="{ident}"' in index_text or "{#" + ident + "}" in index_text:
        return True
    return any(line.lstrip().startswith("#") and ident in line for line in index_text.splitlines())


ANCHOR_ID = re.compile(r'id="([^"]+)"')
HREF = re.compile(r'href="([^"]+)"')


def anchor_ids(text_: str) -> set:
    return set(ANCHOR_ID.findall(text_))


STYLE_BLOCK = re.compile(r"<style>(.*?)</style>", re.S)
CHAR_REF = re.compile(r"&(?:#\d+|#x[0-9a-fA-F]+|[A-Za-z][A-Za-z0-9]*);")
FONT_FAMILY = re.compile(r"font-family:[^;{}]*")


def check_stylesheet(case: str, label: str, doc: str, css: str) -> None:
    """The bundled stylesheet reaches the browser as CSS, not as escaped HTML text (2026-09-18).

    The templates render in an autoescaping environment, which is right for every value that comes from the
    bundle and wrong for the one value that does not: `<style>{{ css }}</style>` wrote
    `font-family: &#34;Segoe UI&#34;, Arial, ...` into report.html. A character reference is HTML text - CSS
    has no such syntax, so the browser drops the whole declaration and lays the page out in its default font
    at other metrics. The existing D4 checks (`size: A4`, no font below 9pt) matched straight through the
    escaping, because neither of those declarations contains a character that autoescaping rewrites.

    Three assertions, because each fails on its own: report.css appears verbatim (nothing was rewritten),
    no character reference is left in the style block (nothing a browser would refuse to parse), and the
    first font-family declaration - the one the defect broke - survives with its quotes intact."""
    if not css.strip():
        check(False, f"{case}: the bundled stylesheet exists at {REPORT_CSS} (a missing file is a failure, not a skip)")
        return
    blocks = STYLE_BLOCK.findall(doc)
    check(bool(blocks), f"{case}: {label} carries a <style> block")
    if not blocks:
        return
    style = blocks[0]
    check(css.strip() in style,
          f"{case}: {label} inlines report.css verbatim - autoescaping the stylesheet is what broke it")
    refs = sorted(set(CHAR_REF.findall(style)))
    expect(refs, [], f"{case}: {label}'s stylesheet carries no HTML character reference", "D4")
    want = FONT_FAMILY.search(css)
    check(want is not None and want.group(0) in style,
          f"{case}: {label} keeps the stylesheet's first font-family declaration, quotes and all "
          f"(want {(want.group(0) if want else None)!r})")


def check_html_index(case: str, out: Path, pres: dict, md: str, html: str, index: str) -> None:
    """The evidence index a browser can read (2026-09-17). A static server hands `.md` to the reader as text:
    no headings, no element to scroll to, so every `evidence-index.md#F-001` link in report.html landed at the
    top of ~38,000px of Markdown source. report.html now links into `evidence-index.html`, rendered from its own
    template beside the Markdown one - so the two must anchor the SAME id set, or they drift apart silently.
    report.md keeps linking to the Markdown index, which stays the data artifact."""
    idx_html_path = out / "evidence-index.html"
    check(idx_html_path.exists(), f"{case}: evidence-index.html written (D4)")
    targets = [t for t in HREF.findall(html) if t.startswith("evidence-index")]
    expect(sorted({t.split("#")[0] for t in targets}), ["evidence-index.html"],
           f"{case}: report.html links only to the browser-readable index")
    if not idx_html_path.exists():
        return
    idx_html = text(idx_html_path)
    md_ids, html_ids = anchor_ids(index), anchor_ids(idx_html)
    expect(sorted(md_ids - html_ids), [], f"{case}: every id the Markdown index anchors is anchored in the HTML index")
    expect(sorted(html_ids - md_ids), [], f"{case}: and the HTML index anchors nothing the Markdown index does not")
    families = {"F-": sorted(f.get("id") for f in pres.get("findings") or []),
                "G-": sorted(g.get("id") for g in pres.get("groups") or []),
                "M-": sorted(m.get("id") for m in pres.get("media") or []),
                "E-": sorted(e.get("id") for e in pres.get("evidence") or []),
                "section": ["top", "missing-results", "imported-evidence"]}
    for family, want in families.items():
        expect([x for x in want if x not in html_ids], [],
               f"{case}: the HTML index anchors every {family} id, case preserved")
    # the defect's own repro, the other way round: follow report.html's links into the HTML index
    frags = sorted({t.split("#", 1)[1] for t in targets if "#" in t})
    expect([f for f in frags if f'id="{f}"' not in idx_html], [],
           f"{case}: every report.html evidence link resolves to a real id= in evidence-index.html")
    # and that resolution check is not vacuous wherever the report has a result to link: a case whose findings
    # are all index-only (R2, R3: every conflict involves neither a target nor a compared patch) links none
    linkable = any(f.get("readerRelevant") and f.get("kind") in ("regression", "finding", "repaired", "reclassified")
                   for f in pres.get("findings") or [])
    if linkable:
        check(bool(frags), f"{case}: report.html carries at least one evidence link to follow")
    # report.md is unchanged: it still points at the data artifact
    check("(evidence-index.html" not in md and "evidence-index.md" in md,
          f"{case}: report.md still links to evidence-index.md")
    # Anchors alone do not protect CONTENT. Every section of the HTML template that carries no id= was
    # invisible to the checks above: deleting the per-finding row tables - the index's own headline promise,
    # "every finding with all of its rows" - left the whole suite green (measured 2026-09-17, review round).
    # Two counting identities catch that: a table, a heading or a set of rows that goes missing in one
    # rendition and not the other changes them. What they do NOT do is prove the two renditions carry the
    # same content - an altered or wrong cell value preserves every count. They are counts, not text,
    # because the two renditions format cells differently on purpose; text equality would be brittle. A
    # content difference that keeps the structure intact is out of this check's reach and stays so.
    # A Markdown table is one separator line, one header line above it, and its data rows; the HTML's
    # <thead> holds that header. Compare DATA rows on both sides.
    pipes = [ln for ln in index.splitlines() if ln.startswith("|")]
    seps = [ln for ln in pipes if set(ln) <= set("|-: ")]
    md_rows = len(pipes) - 2 * len(seps)
    # `<tr[ >]`, not `<tr>`: the group and evidence rows carry the anchor themselves (`<tr id="G-001">`),
    # and a bare `<tr>` pattern silently scores those two whole tables as empty.
    html_rows = len(re.findall(r"<tr[ >]", idx_html)) - len(re.findall(r"<thead>", idx_html))
    expect(html_rows, md_rows, f"{case}: the HTML index carries one table data row per Markdown index data row")
    md_heads = len(re.findall(r"(?m)^#{1,6} ", index))
    html_heads = len(re.findall(r"<h[1-6][ >]", idx_html))
    expect(html_heads, md_heads, f"{case}: the HTML index carries one heading per Markdown index heading")
    arts = {a.get("path") for a in ((load(out / "presentation-manifest.json") or {}).get("artifacts") or [])}
    check("evidence-index.html" in arts, f"{case}: the presentation manifest lists evidence-index.html")


def image_paths(md: str) -> list[str]:
    return [p for p in IMG_MD.findall(md) + IMG_HTML.findall(md) if not p.startswith(("http://", "https://", "data:"))]


def pdf_pages(p: Path) -> int | None:
    if not p.exists():
        return None
    raw = p.read_bytes()
    n = len(re.findall(rb"/Type\s*/Page(?![s/a-zA-Z])", raw))
    if n:
        return n
    counts = [int(x) for x in re.findall(rb"/Count\s+(\d+)", raw)]
    return max(counts) if counts else None


def strip_utc(doc: dict) -> str:
    d = json.loads(json.dumps(doc))
    d.pop("generatedUtc", None)
    return json.dumps(d, sort_keys=True)


def required_missing(disp: dict) -> list[dict]:
    return [m for m in (disp.get("evaluation") or {}).get("missing") or [] if m.get("required") is True]


# ---------------------------------------------------------------------------- the compile steps

#: an app bundle's AI-side overlay bundle, by the app bundle (ai_report_fixtures records it as aiBundle)
AI_OF: dict = {}


def do_plan(bundle: Path, out: Path, exhaustive=False, declare_patches=()):
    args = ["plan", "--bundle", bundle, "--out", out] + (["--exhaustive"] if exhaustive else [])
    if str(bundle) in AI_OF:
        args += ["--ai-bundle", AI_OF[str(bundle)]]
    for name in declare_patches:
        args += ["--declare-patch", name]
    return evalreport(args)


def compile_case(case: str, bundle: Path, out: Path, exhaustive=False, declare_patches=()):
    """plan + report + verify; returns (presentation, plan result, report result, verify result)."""
    p = do_plan(bundle, out, exhaustive, declare_patches)
    if p.returncode != 0:
        print(p.stdout[-2000:]); print(p.stderr[-2000:])
    expect(p.returncode, 0, f"{case}: plan exits 0", "D1")
    pres = load(out / "presentation.json")
    check(pres is not None, f"{case}: presentation.json written")
    r = evalreport(["report", "--presentation", out / "presentation.json", "--out", out])
    if r.returncode != 0:
        print(r.stdout[-2000:]); print(r.stderr[-2000:])
    expect(r.returncode, 0, f"{case}: report exits 0", "D4")
    v = evalreport(["verify", "--out", out])
    if v.returncode != 0:
        print(v.stdout[-2000:]); print(v.stderr[-2000:])
    return pres, p, r, v


# ---------------------------------------------------------------------------- common assertions

def assert_common(case: str, bundle: Path, out: Path, pres: dict, common: dict, verify_res):
    if pres is None:
        return
    c = common["presentation"]
    man = load(bundle / "manifest.json") or {}
    disp = load(bundle / "disposition.json") or {}
    ev = disp.get("evaluation") or {}
    expect(pres.get("schemaVersion"), c["schemaVersion"], f"{case}: presentation.schemaVersion", "D2")
    check(re.fullmatch(c["presentationIdPattern"], str(pres.get("presentationId"))) is not None,
          f"{case}: presentationId is 12 hex (got {pres.get('presentationId')!r}) (D2)")
    src = pres.get("source") or {}
    expect(src.get("bundleSchema"), 2, f"{case}: source.bundleSchema", "D2")
    expect(src.get("planId"), man.get("planId"), f"{case}: source.planId equals the manifest's", "D2")
    expect(src.get("manifestSha256"), sha256_file(bundle / "manifest.json"), f"{case}: source.manifestSha256", "D2")
    expect(src.get("dispositionSha256"), sha256_file(bundle / "disposition.json"), f"{case}: source.dispositionSha256", "D2")
    expect(src.get("ledgerSha256"), sha256_file(bundle / "ledger.json"), f"{case}: source.ledgerSha256", "D2")
    sv = src.get("verify") or {}
    expect(sv.get("bundleComplete"), True, f"{case}: source.verify.bundleComplete", "D1")
    expect(sv.get("evaluationComplete"), ev.get("complete"), f"{case}: source.verify.evaluationComplete = disposition's", "D1")
    expect(sv.get("claim"), ev.get("claim"), f"{case}: source.verify.claim = disposition's", "D1")
    comp = pres.get("compiler") or {}
    expect(comp.get("name"), "evalreport", f"{case}: compiler.name", "D2")
    check("evalreport.py" in (comp.get("sourceSha256") or {}), f"{case}: compiler.sourceSha256 hashes evalreport.py (D2)")
    st = pres.get("settings") or {}
    dflt = c["settingsDefault"]
    if st.get("mode") == "concise":
        for k in ("maxPages", "rowsPerGroup", "figuresPerFinding"):
            expect(st.get(k), dflt[k], f"{case}: settings.{k}", "D2")
    expect(st.get("page"), dflt["page"], f"{case}: settings.page", "D2")
    expect(st.get("reel"), dflt["reel"], f"{case}: settings.reel", "D2")
    # scope
    sc = pres.get("scope") or {}
    comp_ = sc.get("completion") or {}
    expect(comp_.get("complete"), ev.get("complete"), f"{case}: scope.completion.complete = disposition's", "D1")
    expect(comp_.get("claim"), ev.get("claim"), f"{case}: scope.completion.claim = disposition's", "D1")
    req = required_missing(disp)
    if str(bundle) in AI_OF:      # an overlay's missing rows are the evaluation's too (the report joins them)
        req += required_missing(load(Path(AI_OF[str(bundle)]) / "disposition.json") or {})
    expect(comp_.get("missingRequired"), len(req), f"{case}: scope.completion.missingRequired", "D1/D2")
    by = {}
    for m in req:
        by[m.get("status")] = by.get(m.get("status"), 0) + 1
    expect(comp_.get("byCause"), by, f"{case}: scope.completion.byCause (keyed by the missing row's status)", "D2 / EXPECTED.md 2.1")
    plan = load(bundle / "plan.json") or {}
    plan_arms = [a.get("armId") for a in plan.get("arms") or []]
    got_arms = [a.get("armId") for a in sc.get("arms") or []]
    expect(sorted(got_arms), sorted(plan_arms), f"{case}: scope.arms are the plan's arms", "D2")
    check(got_arms[:1] == ["baseline"], f"{case}: the baseline arm is first in scope.arms (D3)")
    baseline_scope = next((a for a in (pres.get("scope") or {}).get("arms") or [] if a.get("armId") == "baseline"), {})
    if (baseline_scope.get("locationPluginScope") or {}).get("complete"):
        recorded = [p.get("name") for p in (((load(bundle / "plan.json") or {}).get("arms") or [{}])[0].get("requested") or {}).get("plugins") or []]
        subset = baseline_scope.get("locationPluginOrder") or []
        expect(subset, [n for n in recorded if n in subset], f"{case}: location contributors preserve recorded relative order")
        check("FixtureBase.esp" in subset and "FixtureTarget.esp" in subset,
              f"{case}: measured non-conflicting location contributors appear in the reader subset")
    target_names = {str(t.get("name")).lower() for t in (pres.get("scope") or {}).get("targets") or [] if t.get("name")}
    for arm in (pres.get("scope") or {}).get("arms") or []:
        if (arm.get("locationPluginScope") or {}).get("basis") != "plugins named by accepted target-conflict evidence":
            continue
        own = set()
        for f in pres.get("findings") or []:
            if f.get("armId") != arm.get("armId") or f.get("part") == "reverse":
                continue
            pair = {str(x) for x in ((f.get("counts") or {}).get("A"), (f.get("counts") or {}).get("B")) if x}
            if {x.lower() for x in pair} & target_names:
                own.update(pair)
        check(set(arm.get("locationPluginOrder") or []) <= own,
              f"{case}: legacy location fallback uses only that arm's non-reverse target conflicts")
    integ = sc.get("integrity") or {}
    expect(integ.get("ok"), True, f"{case}: scope.integrity.ok", "D2")
    expect(integ.get("defects"), [], f"{case}: scope.integrity.defects", "D2")
    # findings
    fs = pres.get("findings") or []
    ids = [f.get("id") for f in fs]
    expect(ids, [f"F-{i + 1:03d}" for i in range(len(fs))], f"{case}: finding ids are F-001.. in order", "D2")
    kinds = set(c["findings"]["kinds"])
    check(all(f.get("kind") in kinds for f in fs), f"{case}: every finding kind is one of D2's (got {sorted({f.get('kind') for f in fs})})")
    pr = [f.get("priority") for f in fs]
    check(all(isinstance(x, int) for x in pr) and pr == sorted(pr), f"{case}: priorities are non-decreasing in list order (D3)")
    evid = {e.get("id"): e for e in pres.get("evidence") or []}
    med = {m.get("id"): m for m in pres.get("media") or [] if m.get("id")}
    grp = {g.get("id"): g for g in pres.get("groups") or []}
    for f in fs:
        fid = f.get("id")
        check(all(e in evid for e in f.get("evidenceIds") or []), f"{case}: {fid} evidenceIds resolve")
        check(all(m in med for m in f.get("mediaIds") or []), f"{case}: {fid} mediaIds resolve")
        check(f.get("groupId") is None or f.get("groupId") in grp, f"{case}: {fid} groupId resolves")
        rows = f.get("rows") or []
        expect(len(rows), f.get("rowsShown"), f"{case}: {fid} len(rows) == rowsShown", "D2")
        check(isinstance(f.get("rowsTotal"), int) and f.get("rowsShown", 0) <= f.get("rowsTotal", 0),
              f"{case}: {fid} rowsShown <= rowsTotal")
        check(all(r.get("evidenceId") in evid for r in rows), f"{case}: {fid} every row's evidenceId resolves")
        check(bool(str(f.get("plain") or "").strip()), f"{case}: {fid} plain sentence is not empty")
        supplied_count = int((f.get("counts") or {}).get("findings") or 0)
        if f.get("kind") == "finding" and supplied_count > 0 and f.get("rowsTotal") == 0:
            check(str(supplied_count) in str(f.get("readerPlain") or ""),
                  f"{case}: rowless imported finding reports its supplied count instead of zero records")
    # evidence (an AI-side overlay's rows are read joined with the app bundle's)
    ai_man = load(Path(AI_OF[str(bundle)]) / "manifest.json") or {} if str(bundle) in AI_OF else {}
    # an overlay row's path is written relative to the app bundle (the report reads both from there)
    ai_rel = os.path.relpath(AI_OF[str(bundle)], bundle).replace("\\", "/") if str(bundle) in AI_OF else ""
    man_ev = {e.get("id"): e for e in man.get("evidence") or []}
    # the overlay's own control, registry, scope and search files are not joined (the app bundle's are the report's)
    man_ev.update({e.get("id"): {**e, "path": f"{ai_rel}/{e.get('path')}"} for e in ai_man.get("evidence") or []
                   if e.get("kind") not in ("control", "registry", "scope", "search")})
    for eid, e in evid.items():
        external_scope = e.get("kind") == "scope" and e.get("recordedBy") == "scopeprobe" and eid[2:] == str(e.get("sha256") or "")[:16]
        ok = re.fullmatch(c["evidence"]["idPattern"], str(eid)) is not None and (eid[2:] in man_ev or external_scope)
        check(ok, f"{case}: evidence {eid} is E-<manifest evidence id> or a content-addressed bound scope")
        if ok:
            if external_scope:
                continue
            check(e.get("path") == man_ev[eid[2:]].get("path") and e.get("sha256") == man_ev[eid[2:]].get("sha256"),
                  f"{case}: evidence {eid} path and sha256 equal the manifest row's")
    # media
    for m in pres.get("media") or []:
        if m.get("assessed") is True:
            check(re.fullmatch(c["media"]["assessedIdPattern"], str(m.get("id"))) is not None
                  and m.get("id") == "M-" + str(m.get("sha256"))[:12],
                  f"{case}: media {m.get('id')} is M-<sha256[:12]>")
            expect(m.get("reason"), None, f"{case}: assessed media {m.get('id')} reason", "D2")
        else:
            check(m.get("reason") in c["media"]["unassessedRowsHaveOneClosedReason"],
                  f"{case}: unassessed media {Path(str(m.get('path'))).name} carries one closed D5 reason (got {m.get('reason')!r})")
    # chapters
    ch = pres.get("chapters") or []
    check(bool(ch), f"{case}: chapters non-empty (D6)")
    if ch:
        expect([x.get("id") for x in ch], [f"C-{i + 1:02d}" for i in range(len(ch))], f"{case}: chapter ids C-01..", "D2")
        expect(ch[0].get("kind"), "opening", f"{case}: first chapter kind", "D6")
        expect(ch[-1].get("kind"), "closing", f"{case}: last chapter kind", "D6")
    fids = set(ids)
    for x in ch:
        check(all(f in fids for f in x.get("findingIds") or []), f"{case}: {x.get('id')} findingIds resolve")
        check(all(m in med for m in x.get("mediaIds") or []), f"{case}: {x.get('id')} mediaIds resolve")
        for cap in x.get("captions") or []:
            check(bool(str(cap.get("text") or "").strip()), f"{case}: {x.get('id')} caption text non-empty")
            check(bool(str(cap.get("narration") or "").strip()), f"{case}: {x.get('id')} caption narration non-empty")
            check(isinstance(cap.get("storyboard"), dict), f"{case}: {x.get('id')} caption has an explicit storyboard")
            check(all(e in evid for e in cap.get("evidenceIds") or []), f"{case}: {x.get('id')} caption evidenceIds resolve")
            check(cap.get("mediaId") is None or cap.get("mediaId") in med, f"{case}: {x.get('id')} caption mediaId resolves")
        check(isinstance(x.get("plannedSeconds"), (int, float)) and x.get("plannedSeconds") > 0,
              f"{case}: {x.get('id')} plannedSeconds > 0")
    nar = pres.get("narration") or {}
    expect(nar.get("source"), "template", f"{case}: narration.source on a fresh plan", "D7")
    expect(nar.get("validated"), True, f"{case}: narration.validated", "D7")
    expect(nar.get("refused"), [], f"{case}: narration.refused on a fresh plan", "D7")
    # forbidden words in the plan's own text (A-4: the registry's own quoted strings are exempt)
    exempt = list(c["forbiddenVerdictWords"]["exemptVerbatimTokens"]) + registry_exemptions(bundle)
    hits = []
    for f in fs:
        hits += [(f.get("id"), h) for h in forbidden_hits(str(f.get("plain") or "") + " " + str(f.get("title") or ""), exempt)]
    for g in pres.get("groups") or []:
        hits += [(g.get("id"), h) for h in forbidden_hits(str(g.get("title") or ""), exempt)]
    for x in ch:
        hits += [(x.get("id"), h) for h in forbidden_hits(str(x.get("title") or ""), exempt)]
        for cap in x.get("captions") or []:
            hits += [(x.get("id"), h) for h in forbidden_hits(str(cap.get("text") or ""), exempt)]
    expect(hits, [], f"{case}: no verdict word (pass/compatible/clean) in plain sentences, titles or captions", "D3")

    # determinism: a second plan into another directory
    out2 = out.parent / (out.name + "-again")
    p2 = do_plan(bundle, out2, exhaustive=(st.get("mode") == "exhaustive"),
                 declare_patches=st.get("declaredPatches") or ())   # the same declaration as the first plan
    pres2 = load(out2 / "presentation.json")
    check(p2.returncode == 0 and pres2 is not None, f"{case}: a second plan runs")
    if pres2:
        expect(strip_utc(pres2), strip_utc(pres), f"{case}: two plans give identical presentation.json apart from generatedUtc", "D2")
        expect(pres2.get("presentationId"), pres.get("presentationId"), f"{case}: presentationId identical", "D2")

    # the report
    r = common["report"]
    for name in r["files"]:
        check((out / name).exists(), f"{case}: {name} written (D4)")
    md = text(out / "report.md")
    index = text(out / "evidence-index.md")
    html = text(out / "report.html")
    pm = load(out / "presentation-manifest.json") or {}
    expect(forbidden_hits(md, list(r["reportMdForbiddenVerdictWords"]["exemptVerbatimTokens"]) + registry_exemptions(bundle)), [],
           f"{case}: report.md carries no verdict word (pass/compatible/clean) outside quoted registry text", "D3 / A-4")
    reader = pres.get("reader") or {}
    check(bool(reader.get("title") and reader.get("scopeLine") and reader.get("verdict")),
          f"{case}: presentation carries a human title, comparison scope and conservative verdict")
    diff_nulls = [f for f in pres.get("findings") or [] if f.get("kind") == "null" and f.get("part") == "diff"]
    if diff_nulls:
        check(bool(reader.get("comparisonSummary")), f"{case}: differential nulls are summarized with patch changes")
        null_text = " ".join(reader.get("nullSummary") or [])
        check(all(str(f.get("readerPlain") or "") not in null_text for f in diff_nulls),
              f"{case}: differential null sentences are absent from the no-problem section")
    check(reader.get("title") in md and reader.get("scopeLine") in md and reader.get("verdict") in md,
          f"{case}: report opens with the reader title, comparison scope and verdict")
    check(all(n in reader.get("scopeLine", "") for n in reader.get("comparisonMods") or []),
          f"{case}: comparison mods are named in the opening scope")
    check(all(n in reader.get("scopeLine", "") for n in reader.get("suppliedPatches") or []),
          f"{case}: supplied patches are named in the opening scope")
    reel_open = ((pres.get("reelNarration") or {}).get("opening") or {})
    check(reader.get("verdict") in str(reel_open.get("narration") or ""),
          f"{case}: reel opening repeats the conservative reader verdict")
    leaked_checks = sorted({m.get("checkId") for m in req if m.get("checkId") in md})
    expect(leaked_checks, [], f"{case}: required-check codes stay in the evidence index")
    absent = [f for f in ids if f not in index]
    expect(absent, [], f"{case}: evidence-index.md names every finding id", "D4")
    absent = [e for e in evid if e not in index]
    expect(absent, [], f"{case}: evidence-index.md names every evidence id", "D4")
    absent = [m.get("id") for m in pres.get("media") or [] if m.get("assessed") is True and str(m.get("sha256")) not in index]
    expect(absent, [], f"{case}: evidence-index.md carries every assessed media sha256", "D4")
    toks = sorted(set(TOKEN.findall(md)))
    expect(toks, [], f"{case}: internal evidence tokens stay out of the reader report")
    unresolved = [t for t in toks if not has_anchor(index, t)]
    expect(unresolved, [], f"{case}: every token in report.md resolves to an anchor in evidence-index.md", "D4")
    imgs = image_paths(md)
    missing_imgs = [p for p in imgs if not (out / p).exists()]
    expect(missing_imgs, [], f"{case}: every relative image path in report.md exists under OUT", "D4")
    check_html_index(case, out, pres, md, html, index)
    check("size: A4" in html.replace("size:A4", "size: A4"), f"{case}: report.html has @page size A4 (D4)")
    small = [m for m in re.findall(r"font-size\s*:\s*([0-9.]+)pt", html) if float(m) < 9]
    expect(small, [], f"{case}: report.html has no font below 9pt", "D4")
    # ...and the stylesheet those two read is the file, unchanged - asserted on what `evalreport report`
    # actually wrote here, not on a template rendered in the test
    css_text = text(REPORT_CSS)
    check_stylesheet(case, "report.html", html, css_text)
    check_stylesheet(case, "evidence-index.html", text(out / "evidence-index.html"), css_text)
    arts = pm.get("artifacts") or []
    check(bool(arts), f"{case}: presentation-manifest lists produced artifacts (D4)")
    bad = []
    for a in arts if isinstance(arts, list) else [dict(path=k, **(v if isinstance(v, dict) else {})) for k, v in arts.items()]:
        p = a.get("path") or a.get("name")
        if p and (out / p).exists() and a.get("sha256") and sha256_file(out / p) != a.get("sha256"):
            bad.append(p)
    expect(bad, [], f"{case}: every artifact's sha256 matches the file", "D4")
    support = pm.get("supportingEvidence") or []
    for e in support:
        check((out / e["path"]).exists() and sha256_file(out / e["path"]) == e.get("sha256"),
              f"{case}: copied supporting scope evidence exists and matches the presentation manifest")
    pr_ = pm.get("print") or {}
    expect(pr_.get("maxPages"), r["manifest"]["print"]["maxPages"], f"{case}: print.maxPages", "D4")
    check(pr_.get("status") in r["manifest"]["print"]["statusWords"], f"{case}: print.status is a D4 word (got {pr_.get('status')!r})")
    if pr_.get("status") != "not-rendered":
        expect(pdf_pages(out / "report.pdf"), pr_.get("pages"), f"{case}: print.pages equals the PDF's own page count", "D4")
    # verify
    expect(verify_res.returncode, 0, f"{case}: evalreport verify --out OUT exits 0", "D4")
    # relocation
    moved = out.parent / (out.name + "-moved") / "deeper"
    moved.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(str(out), str(moved))
    v2 = evalreport(["verify", "--out", moved])
    expect(v2.returncode, 0, f"{case}: verify exits 0 after the OUT directory is moved", "D4")
    md2 = text(moved / "report.md")
    index2 = text(moved / "evidence-index.md")
    unresolved = [t for t in sorted(set(TOKEN.findall(md2))) if not has_anchor(index2, t)]
    expect(unresolved, [], f"{case}: tokens still resolve after relocation", "D4")
    missing_imgs = [p for p in image_paths(md2) if not (moved / p).exists()]
    expect(missing_imgs, [], f"{case}: image paths still exist after relocation", "D4")
    if support:
        sp = moved / support[0]["path"]
        original = sp.read_bytes()
        sp.write_bytes(original + b"\nchanged")
        changed = evalreport(["verify", "--out", moved])
        check(changed.returncode != 0 and "supporting-evidence-changed" in changed.stderr,
              f"{case}: verify refuses a changed copied scope document")
        sp.write_bytes(original)
    shutil.move(str(moved), str(out))
    return md, index


def headline(reader) -> str | None:
    """The comparison sentence (a patch or alternative setup against the measured order). The reader's verdict leads
    with the prediction; this sentence follows it as the validation headline, in the same words as before."""
    return ((reader or {}).get("validation") or {}).get("headline")


def finding_where(pres, **kv):
    out = []
    for f in pres.get("findings") or []:
        if all(f.get(k) == v for k, v in kv.items()):
            out.append(f)
    return out


def numbers_in(obj) -> set:
    out = set()
    if isinstance(obj, dict):
        for v in obj.values():
            out |= numbers_in(v)
    elif isinstance(obj, list):
        for v in obj:
            out |= numbers_in(v)
    elif isinstance(obj, (int, float)) and not isinstance(obj, bool):
        out.add(obj)
    return out


def chapter_captions(pres, kinds) -> str:
    return " ".join(str(cap.get("text") or "") for x in pres.get("chapters") or [] if x.get("kind") in kinds
                    for cap in x.get("captions") or [])


def check_direction_inference():
    spec = importlib.util.spec_from_file_location("evalreport_under_test", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    finding = {"counts": {"groupKind": "CLOBBERED", "A": "Earlier.esp", "B": "Later.esp"},
               "rows": [{"summary": "1 navmesh record(s) re-cut by Earlier.esp and then re-cut again by Later.esp, which declares no dependency on it",
                         "physicalDetail": "victim-only ground on the loser's mesh farther from the winner's (Later.esp)"}]}
    got = module.infer_clobber_direction(finding, ["Base.esm", "Earlier.esp", "Later.esp"])
    expect((got or {}).get("overwrittenPlugin"), "Earlier.esp", "direction: evidence names the overwritten plugin")
    expect((got or {}).get("winningPlugin"), "Later.esp", "direction: evidence and order name the later winner")
    expect(module.infer_clobber_direction(finding, ["Later.esp", "Earlier.esp"]), None,
           "direction: contradictory recorded order prevents a directional claim")
    reversed_finding = {**finding, "rows": [{"summary": "1 navmesh record(s) re-cut by Later.esp and then re-cut again by Earlier.esp, which declares no dependency on it",
                                              "physicalDetail": "winner's (Later.esp)"}]}
    expect(module.infer_clobber_direction(reversed_finding, ["Earlier.esp", "Later.esp"]), None,
           "direction: a reversed source sentence cannot be inferred from loose name matching")


def check_stylesheet_escaping():
    """One template, two kinds of value: the stylesheet is markup we ship and must not be escaped; everything
    the bundle supplies is data and must be. Rendered from the templates alone - no fixture, no compiler run -
    so the control runs even when one case is named on the command line, and so the two halves are asserted
    against the same rendering.

    The second half is the reason `|safe` on `css` is safe to state: it is applied to one value read from the
    templates folder, and a report value carrying markup still arrives escaped."""
    label = "stylesheet-escaping"
    spec = importlib.util.spec_from_file_location("evalreport_stylesheet", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    css = text(REPORT_CSS)
    markup = 'Fixture <b>"&" mod</b>'                       # a report value that looks like markup
    arm = {"armId": "baseline", "displayLabel": markup, "readerLabel": markup, "pluginsAdded": [],
           "pluginsRemoved": [], "readerLimits": [], "locationPluginOrder": [],
           "locationPluginScope": {"locations": [], "complete": True, "missingInput": ""}}
    shown = {"layout": "normal", "findings": [], "findingBlocks": [], "scope": {"arms": [arm]},
             "reader": {"title": markup, "scopeLine": "", "verdict": "", "limitations": [],
                        "context": {"evaluatedAt": None, "statesExamined": [], "statesNotExamined": []},
                        "locationOrder": {"heading": "", "intro": "", "empty": ""},
                        "sectionHeadings": {"regressions": "Problems introduced", "changes": "What changed",
                                            "prediction": "Prediction", "coverage": "Coverage",
                                            "validation": "Validation"},
                        "prediction": {"verdict": markup, "items": [], "noneFoundBasis": None, "missingFacts": [],
                                       "record": [], "predictionArm": "baseline"},
                        "coverage": {"lines": [], "notes": [], "setAsideLine": None},
                        "validation": {"status": "no-patch", "sentence": "", "other": ""},
                        "checkTitles": {}, "checkExplanations": {}, "comparisonSummary": [],
                        "missingSummary": [], "nullSummary": [], "catalogueSummary": [], "otherLimits": []}}
    try:
        html = module.env(True).get_template("report.html.j2").render(p=shown, css=css)
    except Exception as ex:                                  # StrictUndefined: a new template field, not a skip
        check(False, f"{label}: report.html.j2 renders from this control's view ({type(ex).__name__}: {ex})")
        return
    check_stylesheet(label, "report.html", html, css)
    # the negative control: autoescaping is still on for everything that came from the bundle
    check(markup not in html and "<b>" not in html,
          f"{label}: a report value containing markup is not written into report.html raw")
    check("&lt;b&gt;" in html and "&amp;" in html,
          f"{label}: it is written escaped - the stylesheet is the only unescaped value in the template")


PATCH_OUTCOMES = {
    # the orderEvaluation.perProbe placed on every alternative arm; contactprobe's diff in R4 is unchanged, so its
    # reader counts come from these numbers, and overlapprobe's candidate-02 diff reports one earlier pair gone
    "contact-regression": {"contactprobe": {"repaired": 0, "created": 2, "newDefect": 2}},
    "contact-repair": {"contactprobe": {"repaired": 2, "created": 0, "newDefect": 0}},
    "contact-regression-repair": {"contactprobe": {"repaired": 1, "created": 2, "newDefect": 2}},
    "overlap-regression": {"overlapprobe": {"repaired": 0, "created": 1, "newDefect": 1}},
    "overlap-reduction": {"overlapprobe": {"repaired": 1, "created": 0, "newDefect": 0}},
}


def patch_direction_control(bundle: Path, variant: str, outcome: str = "contact-regression"):
    """The reference patch is named, and each comparison says what it changes, in EITHER direction. Built in
    memory from R4's bundle, as the overlap wording control is: the arms' requested plugin lists are rewritten
    and one PATCH_OUTCOMES result (default: a contact-check regression) is placed on every alternative arm.

      removes  the measured load order loads Patch.esp; the alternative removes it (a removal arm)
      adds     the alternative is a supplied combination that adds Patch.esp
      none     the alternative removes only the target, so no separate patch distinguishes the arms
      mixed    Other.esp is in the measured order; one alternative adds Patch.esp, another removes Other.esp
      listed   the alternative removes FixtureBase.esp, which the target's recorded conflicts already name
      both     Other.esp is in the measured order; the one alternative adds Patch.esp AND removes Other.esp
      both-one as both, but only Patch.esp is declared a patch

    Returns (plan, report.md, report.html)."""
    spec = importlib.util.spec_from_file_location(f"evalreport_patch_direction_{variant}_{outcome}", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    view = module.Bundle(bundle)
    view.disposition = copy.deepcopy(view.disposition)
    view.plan = copy.deepcopy(view.plan)
    wanted = {"baseline", "candidate-02-patchb"} | ({"candidate-01-patcha"} if variant == "mixed" else set())
    view.disposition["arms"] = {k: v for k, v in view.disposition["arms"].items() if k in wanted}
    view.plan["arms"] = [a for a in view.plan.get("arms") or [] if a.get("armId") in wanted]
    evaluation = view.disposition["evaluation"]
    for key in ("missing", "findings", "completedNullChecks", "completedCatalogues"):
        evaluation[key] = [row for row in evaluation.get(key) or [] if row.get("armId") in wanted]
    arms = {a["armId"]: a for a in view.plan["arms"]}
    base = list(arms["baseline"]["requested"]["plugins"])

    def plugin(name):
        row = copy.deepcopy(base[-1])
        row["name"] = name
        return row

    without_target = [p for p in base if p.get("name") != "FixtureTarget.esp"]
    orders = {"removes": {"baseline": base + [plugin("Patch.esp")], "candidate-02-patchb": base},
              "adds": {"baseline": base, "candidate-02-patchb": base + [plugin("Patch.esp")]},
              "none": {"baseline": base, "candidate-02-patchb": without_target},
              "listed": {"baseline": base, "candidate-02-patchb": [p for p in base if p.get("name") != "FixtureBase.esp"]},
              "mixed": {"baseline": base + [plugin("Other.esp")],
                        "candidate-01-patcha": base + [plugin("Other.esp"), plugin("Patch.esp")],
                        "candidate-02-patchb": base},
              "both": {"baseline": base + [plugin("Other.esp")], "candidate-02-patchb": base + [plugin("Patch.esp")]},
              "both-one": {"baseline": base + [plugin("Other.esp")], "candidate-02-patchb": base + [plugin("Patch.esp")]}}[variant]
    for arm_id, plugins in orders.items():
        arms[arm_id]["requested"]["plugins"] = plugins
    # Patch identity is declared, never inferred from membership (636ae180): a variant whose
    # alternative changes a patch declares it in the plan's `patches` list, as a real plan does.
    view.plan["patches"] = {"removes": ["Patch.esp"], "adds": ["Patch.esp"],
                            "mixed": ["Other.esp", "Patch.esp"], "both": ["Other.esp", "Patch.esp"],
                            "both-one": ["Patch.esp"]}.get(variant, [])
    if variant == "adds":
        arms["candidate-02-patchb"]["kind"] = "supplied-combination"
        view.disposition["arms"]["candidate-02-patchb"]["kind"] = "supplied-combination"
    per_probe = PATCH_OUTCOMES[outcome]
    regression = any(p["newDefect"] for p in per_probe.values())
    for arm_id in wanted - {"baseline"}:
        view.disposition["arms"][arm_id]["orderEvaluation"] = {
            "classification": "REGRESSION" if regression else "PASSES-COMPLETED-CHECKS", "regression": regression,
            "probes": sorted(per_probe),
            "perProbe": {probe: {"verdict": "DIFFERENTIAL-VERIFIED", "reclassified": 0, "attributed": 0, "unmeasured": 0, **p}
                         for probe, p in per_probe.items()}}
    priorities = module.load_json(module.PRIORITIES)
    concise = priorities.get("concise") or {}
    page = priorities.get("page") or {}
    settings = {"mode": "concise", "layout": concise.get("layout", "normal"), "maxPages": 8,
                "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                         "baseFontPt": page.get("baseFontPt", 10.5)},
                "rowsPerGroup": concise.get("rowsPerGroup", 3),
                "figuresPerFinding": concise.get("figuresPerFinding", 1),
                "reel": priorities.get("reel") or {}}
    original_bundle = module.Bundle
    try:
        module.Bundle = lambda _: view
        plan = module.compile_plan(bundle, {"bundle": {"complete": True},
                                            "evaluation": {"complete": False, "claim": "incomplete"}},
                                   settings, priorities, str(bundle))
    finally:
        module.Bundle = original_bundle
    shown = module.effective_view(plan, settings["rowsPerGroup"], settings["figuresPerFinding"], priorities,
                                  settings["layout"])
    md = module.env(False).get_template("report.md.j2").render(p=shown, css="")
    html = module.env(True).get_template("report.html.j2").render(p=shown, css="")
    return plan, md, html


def check_patch_direction(bundle: Path):
    """Known answers for the patch named in the title and opening, the direction each Result / finding sentence
    states, and the local load-order list's own statement of what it lists."""
    label = "R4/patch-direction"
    only_listed = "This list names only plugins that appear in recorded conflicts involving the target mods."
    loads_patch = "This setup also loads Patch.esp, which is not listed."

    def arm(plan, arm_id):
        return next((a for a in (plan.get("scope") or {}).get("arms") or [] if a.get("armId") == arm_id), {})

    def missing_input(plan, arm_id):
        return str((arm(plan, arm_id).get("locationPluginScope") or {}).get("missingInput") or "")

    def regression_plain(plan, arm_id):
        return next((str(f.get("readerPlain") or "") for f in plan.get("findings") or []
                     if f.get("kind") == "regression" and f.get("armId") == arm_id), "")

    # (a) the measured load order has the patch; the alternative removes it
    plan, md, html = patch_direction_control(bundle, "removes")
    reader = plan.get("reader") or {}
    expect(reader.get("title"), "FixtureTarget.esp with Patch.esp — patch review",
           f"{label}/removes: the title names the patch the alternative removes")
    expect(reader.get("suppliedPatches"), ["Patch.esp"], f"{label}/removes: the patch is derived from the removal arm")
    check("The patch evaluated is Patch.esp, included in the measured load order and removed for comparison."
          in str(reader.get("scopeLine")) and "No separate patch plugin" not in str(reader.get("scopeLine")),
          f"{label}/removes: the opening names the patch and says the comparison removes it (got {reader.get('scopeLine')!r})")
    expect(headline(reader), "Removing Patch.esp introduces 2 new problems in the check \"Objects left without their supporting surface\".",
           f"{label}/removes: the Result says the defect comes with removing the patch")
    check(regression_plain(plan, "candidate-02-patchb").startswith("Removing Patch.esp introduced 2 new problems"),
          f"{label}/removes: the finding sentence names the removal (got {regression_plain(plan, 'candidate-02-patchb')!r})")
    check("## Problems introduced by removing Patch.esp" in md and "Problems introduced by removing Patch.esp" in html,
          f"{label}/removes: the problems heading names the removal in report.md and report.html")
    check("supplied setup" not in md.lower() and "supplied setup" not in html.lower(),
          f"{label}/removes: a removal arm is never called the supplied setup (report.md, report.html)")
    check(only_listed in missing_input(plan, "baseline") and loads_patch in missing_input(plan, "baseline"),
          f"{label}/removes: the measured order's list says what it lists and names the patch it loads but omits "
          f"(got {missing_input(plan, 'baseline')!r})")
    check(only_listed in missing_input(plan, "candidate-02-patchb") and "Patch.esp" not in missing_input(plan, "candidate-02-patchb"),
          f"{label}/removes: the alternative, which does not load the patch, does not claim to load it")
    expect(md.count(loads_patch), 1, f"{label}/removes: report.md names the omitted patch once, for the arm that loads it")

    # (b) a supplied combination adds the patch
    plan, md, html = patch_direction_control(bundle, "adds")
    reader = plan.get("reader") or {}
    expect(reader.get("title"), "FixtureTarget.esp with Patch.esp — patch review",
           f"{label}/adds: the title names the patch the supplied combination adds")
    check("The patch evaluated is Patch.esp, added to the measured load order for comparison." in str(reader.get("scopeLine"))
          and "removed for comparison" not in str(reader.get("scopeLine")),
          f"{label}/adds: the opening names the patch and says the comparison adds it (got {reader.get('scopeLine')!r})")
    expect(headline(reader), "Adding Patch.esp introduces 2 new problems in the check \"Objects left without their supporting surface\".",
           f"{label}/adds: the Result says the defect comes with adding the patch")
    check(regression_plain(plan, "candidate-02-patchb").startswith("Adding Patch.esp introduced 2 new problems"),
          f"{label}/adds: the finding sentence names the addition (got {regression_plain(plan, 'candidate-02-patchb')!r})")
    check("## Problems introduced by adding Patch.esp" in md and "Problems introduced by adding Patch.esp" in html,
          f"{label}/adds: the problems heading names the addition in report.md and report.html")
    expect(arm(plan, "candidate-02-patchb").get("displayLabel"), "Setup including supplied patch: Patch.esp",
           f"{label}/adds: the supplied combination keeps its label")
    check(loads_patch in missing_input(plan, "candidate-02-patchb") and "Patch.esp" not in missing_input(plan, "baseline"),
          f"{label}/adds: only the arm that loads the patch names it as omitted from its list")

    # (c) no separate patch distinguishes the arms: the alternative removes the target itself
    plan, md, html = patch_direction_control(bundle, "none")
    reader = plan.get("reader") or {}
    expect(reader.get("suppliedPatches"), [], f"{label}/none: removing a target is not a patch")
    check("No separate patch plugin was declared for this comparison." in str(reader.get("scopeLine")),
          f"{label}/none: the opening keeps the no-separate-patch wording (got {reader.get('scopeLine')!r})")
    # the title with no separate patch: followup_title_control
    expect(headline(reader), "Removing FixtureTarget.esp introduces 2 new problems in the check \"Objects left without their supporting surface\".",
           f"{label}/none: the Result names the removal even when no patch is involved")
    check("supplied setup" not in md.lower() and "This setup also loads" not in md,
          f"{label}/none: a removal arm is not the supplied setup, and no omitted patch is claimed")

    # (d) mixed: one alternative adds a patch, another removes a different one
    plan, md, html = patch_direction_control(bundle, "mixed")
    reader = plan.get("reader") or {}
    expect(reader.get("suppliedPatches"), ["Other.esp", "Patch.esp"], f"{label}/mixed: both distinguishing plugins")
    expect(reader.get("title"), "FixtureTarget.esp with Other.esp, Patch.esp — patch review", f"{label}/mixed: title")
    check("The patches evaluated are Other.esp, Patch.esp, added or removed for comparison." in str(reader.get("scopeLine")),
          f"{label}/mixed: the opening says the comparisons add or remove them (got {reader.get('scopeLine')!r})")
    expect(headline(reader), "A tested alternative setup introduces new problems in the check \"Objects left without their supporting surface\" in 2 setups (2, 2).",
           f"{label}/mixed: arms that change different things share no single subject, and one removes a plugin")
    check(regression_plain(plan, "candidate-01-patcha").startswith("Adding Patch.esp introduced")
          and regression_plain(plan, "candidate-02-patchb").startswith("Removing Other.esp introduced"),
          f"{label}/mixed: each finding sentence names its own arm's change")
    check("## Problems introduced by a tested alternative setup" in md and "supplied setup" not in md.lower(),
          f"{label}/mixed: the problems heading does not call a set that includes a removal the supplied setup")

    # (e) the compared plugin is already in the measured order's list: nothing is claimed omitted
    plan, md, html = patch_direction_control(bundle, "listed")
    check("FixtureBase.esp" in (arm(plan, "baseline").get("locationPluginOrder") or [])
          and only_listed in missing_input(plan, "baseline") and "This setup also loads" not in missing_input(plan, "baseline"),
          f"{label}/listed: a compared plugin the list already shows is not reported as omitted "
          f"(got {arm(plan, 'baseline').get('locationPluginOrder')!r} / {missing_input(plan, 'baseline')!r})")

    def plain_of(plan, arm_id, kinds, check_id):
        return " ".join(str(f.get("readerPlain") or "") for f in plan.get("findings") or []
                        if f.get("armId") == arm_id and f.get("kind") in kinds and f.get("checkId") == check_id)

    subjects = {"adds": "Adding Patch.esp", "removes": "Removing Patch.esp", "mixed": "A tested alternative setup"}

    # (f) overlap-only results name the change in both directions: the skill's overlap wording is bound to it
    for variant in ("adds", "removes"):
        subject = subjects[variant]
        plan, md, html = patch_direction_control(bundle, variant, "overlap-regression")
        verdict = str(headline(plan.get("reader")) or "")
        check(verdict.startswith(f"{subject} introduces 1 new pair of overlapping objects for visual review."),
              f"{label}/{variant}/overlap-regression: the overlap-only Result names the change (got {verdict!r})")
        got = plain_of(plan, "candidate-02-patchb", ("regression",), "geometry.object-overlap")
        check(got.startswith(f"{subject} introduced 1 new pair of overlapping objects for visual review."),
              f"{label}/{variant}/overlap-regression: the overlap finding names the change (got {got!r})")
        check("supplied setup" not in md.lower() and "supplied setup" not in html.lower(),
              f"{label}/{variant}/overlap-regression: no supplied-setup wording in report.md or report.html")
        plan, md, html = patch_direction_control(bundle, variant, "overlap-reduction")
        verdict = str(headline(plan.get("reader")) or "")
        check(verdict.startswith(f"{subject} reduces the number of pairs of overlapping objects reported by the checks that ran."),
              f"{label}/{variant}/overlap-reduction: the overlap-only Result names the change (got {verdict!r})")
        got = plain_of(plan, "candidate-02-patchb", ("repaired", "reclassified"), "geometry.object-overlap")
        check(f"no longer present after {subject[:1].lower() + subject[1:]}" in got,
              f"{label}/{variant}/overlap-reduction: the overlap reduction names the change (got {got!r})")
        check("supplied setup" not in md.lower() and "supplied setup" not in html.lower(),
              f"{label}/{variant}/overlap-reduction: no supplied-setup wording in report.md or report.html")

    # (g) the repaired-only and regression-plus-repair Results, adding / removing / several arms
    for variant in ("adds", "removes", "mixed"):
        subject = subjects[variant]
        plan, md, html = patch_direction_control(bundle, variant, "contact-repair")
        expect(headline(plan.get("reader")),
               f"{subject} resolves some measured conflicts and no new defect was reported by the checks that ran.",
               f"{label}/{variant}/contact-repair: the repaired-only Result names the change")
        plan, md, html = patch_direction_control(bundle, variant, "contact-regression-repair")
        expect(headline(plan.get("reader")),
               f"{subject} resolves some measured conflicts, but it also introduces "
               + ("new problems in the check \"Objects left without their supporting surface\" in 2 setups (2, 2)."
                  if variant == "mixed" else
                  "2 new problems in the check \"Objects left without their supporting surface\"."),
               f"{label}/{variant}/contact-regression-repair: the regression-plus-repair Result names the change")

    # (h) one arm that adds one plugin and removes another names both changes
    plan, md, html = patch_direction_control(bundle, "both")
    reader = plan.get("reader") or {}
    expect(headline(reader), "Adding Patch.esp and removing Other.esp introduces 2 new problems in the check \"Objects left without their supporting surface\".",
           f"{label}/both: the Result names the addition and the removal")
    # One arm that adds and removes is "mixed": neither declared patch is evaluated on its own, and the opening
    # says so and names them. It must never claim no patch was declared (ruling 2026-10-05, option 2).
    check("The tested setup changes the declared patches Other.esp, Patch.esp at once, so none of them is evaluated on its own."
          in str(reader.get("scopeLine")) and "No separate patch plugin" not in str(reader.get("scopeLine")),
          f"{label}/both: the opening names the declared patches the setup changes at once (got {reader.get('scopeLine')!r})")
    expect(reader.get("suppliedPatches"), [], f"{label}/both: a mixed arm evaluates no patch on its own")
    # the same arm with only Patch.esp declared: Other.esp is an undeclared plugin that moves with it
    plan, md, html = patch_direction_control(bundle, "both-one")
    scope = str((plan.get("reader") or {}).get("scopeLine"))
    check("The tested setup changes the declared patch Patch.esp together with another plugin, so it is not evaluated "
          "on its own." in scope and "No separate patch plugin" not in scope and "Other.esp at once" not in scope,
          f"{label}/both-one: one declared patch in a mixed arm is named alone (got {scope!r})")
    check("## Problems introduced by adding Patch.esp and removing Other.esp" in md and "supplied setup" not in md.lower(),
          f"{label}/both: the problems heading names both changes")

    # (i) the setups-compared row joins its clauses without a stray separator, in both directions
    check(": adds Patch.esp; removes Other.esp." in md and ": adds Patch.esp; removes Other.esp." in html,
          f"{label}/both: an arm that adds and removes lists both, separated by a semicolon")
    plan, md, html = patch_direction_control(bundle, "removes")
    check("- **Tested alternative setup**: removes Patch.esp." in md
          and "<strong>Tested alternative setup</strong>: removes Patch.esp." in html,
          f"{label}/removes: a removal-only row reads 'Tested alternative setup: removes …' with no leading semicolon")


def alternative_identity_control(bundle: Path, identical: bool = False):
    """Several alternatives that only REMOVE plugins share one display label. Built in memory from R4's bundle:
    the measured load order loads FixtureTarget.esp and FixtureSecond.esp (both targets); candidate-01 removes
    both, candidate-02 removes FixtureSecond.esp, candidate-03 removes FixtureTarget.esp. The results are placed
    directly: the record-link check finds a problem on candidate-01 and none on candidates 02 and 03; the
    required-plugins check finds a problem on candidates 01 and 03 and none on candidate-02; the contact
    comparison adds no problem on candidates 02 and 03. `identical`: candidate-03 removes FixtureSecond.esp too,
    so two alternatives share both the label and the change. Returns (plan, report.md, report.html)."""
    spec = importlib.util.spec_from_file_location("evalreport_alternative_identity", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    view = module.Bundle(bundle)
    view.disposition = copy.deepcopy(view.disposition)
    view.plan = copy.deepcopy(view.plan)
    arms = {a["armId"]: a for a in view.plan["arms"]}
    base = list(arms["baseline"]["requested"]["plugins"])
    second = copy.deepcopy(next(p for p in base if p.get("name") == "FixtureTarget.esp"))
    second["name"] = "FixtureSecond.esp"
    target_row = copy.deepcopy(next(t for t in view.plan["targets"] if t.get("name") == "FixtureTarget.esp"))
    target_row["name"] = "FixtureSecond.esp"
    view.plan["targets"].append(target_row)
    measured = base + [second]
    # identical="nochange": candidates 02 and 03 both keep the measured plugin list (the same label, no change)
    orders = {"baseline": measured,
              "candidate-01-patcha": [p for p in measured if p.get("name") not in ("FixtureTarget.esp", "FixtureSecond.esp")],
              "candidate-02-patchb": (list(measured) if identical == "nochange" else
                                      [p for p in measured if p.get("name") != "FixtureSecond.esp"]),
              "candidate-03-combined": (list(measured) if identical == "nochange" else
                                        [p for p in measured
                                         if p.get("name") != ("FixtureSecond.esp" if identical else "FixtureTarget.esp")])}
    for arm_id, plugins in orders.items():
        arms[arm_id]["requested"]["plugins"] = plugins
        arms[arm_id]["kind"] = "baseline" if arm_id == "baseline" else "candidate"
        view.disposition["arms"][arm_id]["kind"] = arms[arm_id]["kind"]
        view.disposition["arms"][arm_id].pop("orderEvaluation", None)
    used = {"loadability.dangling-references", "loadability.masters-and-order", "geometry.placement-contact"}
    evaluation = view.disposition["evaluation"]
    evaluation["missing"] = [r for r in evaluation.get("missing") or [] if r.get("checkId") not in used]
    evaluation["findings"] = [
        {"checkId": "loadability.dangling-references", "armId": "candidate-01-patcha", "part": None, "findings": 1,
         "verdict": None, "strength": "calibrated-inference"},
        {"checkId": "loadability.masters-and-order", "armId": "candidate-01-patcha", "part": None, "findings": 1,
         "verdict": None, "strength": "calibrated-inference"},
        {"checkId": "loadability.masters-and-order", "armId": "candidate-03-combined", "part": None, "findings": 1,
         "verdict": None, "strength": "calibrated-inference"}]
    evaluation["completedNullChecks"] = [
        {"checkId": "loadability.dangling-references", "armId": "candidate-02-patchb", "part": None, "coverage": 0},
        {"checkId": "loadability.dangling-references", "armId": "candidate-03-combined", "part": None, "coverage": 0},
        {"checkId": "loadability.masters-and-order", "armId": "candidate-02-patchb", "part": None, "coverage": 0},
        {"checkId": "geometry.placement-contact", "armId": "candidate-02-patchb", "part": "diff", "coverage": 0},
        {"checkId": "geometry.placement-contact", "armId": "candidate-03-combined", "part": "diff", "coverage": 0}]
    priorities = module.load_json(module.PRIORITIES)
    concise = priorities.get("concise") or {}
    page = priorities.get("page") or {}
    settings = {"mode": "concise", "layout": concise.get("layout", "normal"), "maxPages": 8,
                "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                         "baseFontPt": page.get("baseFontPt", 10.5)},
                "rowsPerGroup": concise.get("rowsPerGroup", 3),
                "figuresPerFinding": concise.get("figuresPerFinding", 1),
                "reel": priorities.get("reel") or {}}
    original_bundle = module.Bundle
    try:
        module.Bundle = lambda _: view
        plan = module.compile_plan(bundle, {"bundle": {"complete": True},
                                            "evaluation": {"complete": False, "claim": "incomplete"}},
                                   settings, priorities, str(bundle))
    finally:
        module.Bundle = original_bundle
    shown = module.effective_view(plan, settings["rowsPerGroup"], settings["figuresPerFinding"], priorities,
                                  settings["layout"])
    md = module.env(False).get_template("report.md.j2").render(p=shown, css="")
    html = module.env(True).get_template("report.html.j2").render(p=shown, css="")
    return plan, md, html


def check_alternative_identity(bundle: Path):
    """Known answers: when alternatives share a display label, every per-setup sentence says WHICH alternative, in
    the words the setups-compared list uses, and a no-problem line is never merged across arms whose results
    differ. A single alternative keeps its bare label."""
    label = "R4/alternative-identity"
    plan, md, html = alternative_identity_control(bundle)
    reader = plan.get("reader") or {}
    changes = {"candidate-01-patcha": "removes FixtureTarget.esp, FixtureSecond.esp",
               "candidate-02-patchb": "removes FixtureSecond.esp",
               "candidate-03-combined": "removes FixtureTarget.esp"}
    for arm_id, change in changes.items():
        check(f"- **Tested alternative setup**: {change}." in md
              and f"<strong>Tested alternative setup</strong>: {change}." in html,
              f"{label}: the setups-compared row for {arm_id} keeps its label and reads '{change}'")

    def names(line, arm_id):
        # the arm's change as a whole phrase: a shorter change is a prefix of candidate-01's, so it must END there
        return re.search(re.escape(changes[arm_id]) + r"(?!,\s*[\w-]|[\w-])", line) is not None

    def section(text, heading, stop):
        start = text.find(heading)
        end = text.find(stop, start + len(heading)) if start >= 0 else -1
        return text[start:end] if start >= 0 and end >= 0 else ""

    null_lines = [str(x) for x in reader.get("nullSummary") or []]
    bare = [x for x in null_lines if "— Tested alternative setup:" in x]
    check(not bare, f"{label}: no no-problem line names a setup by the bare shared label (got {bare!r})")
    links = [x for x in null_lines if x.startswith("Missing or broken record links — ")]
    expect(len(links), 2, f"{label}: the record-link check has one no-problem line per clean arm, never merged")
    check(any(names(x, "candidate-02-patchb") for x in links) and any(names(x, "candidate-03-combined") for x in links),
          f"{label}: the record-link no-problem lines name candidate-02's and candidate-03's changes (got {links!r})")
    check(not any(names(x, "candidate-01-patcha") for x in links),
          f"{label}: the arm whose record-link check found a problem is never described as clean (got {links!r})")
    masters = [x for x in null_lines if x.startswith("Whether the tested load order can resolve its required plugins — ")]
    check(len(masters) == 1 and names(masters[0], "candidate-02-patchb")
          and not names(masters[0], "candidate-01-patcha") and not names(masters[0], "candidate-03-combined"),
          f"{label}: a check clean on exactly one arm names that arm alone (got {masters!r})")
    # the concise page prints a no-problem line in its short form (skill data concise.nullLong -> nullShort, W-B1)
    conc_ = (json.loads((reporting_data.PRIORITIES)
                        .read_text(encoding="utf-8")).get("concise") or {})
    links = [x.replace(conc_.get("nullLong") or "\0", conc_.get("nullShort") or "") for x in links]
    masters = [x.replace(conc_.get("nullLong") or "\0", conc_.get("nullShort") or "") for x in masters]
    md_null = section(md, "## Checks that found no problem", "## Reference information collected")
    html_null = section(html, "<h2>Checks that found no problem</h2>", "</ul>")
    def on_page(text, x, esc=lambda v: v):
        # the page states a check's result once for the setups it shares (P64 lane, page budget): each plan line's
        # check, setup and result are on one line of the page, the setup named in full
        check_, rest = x.split(" — ", 1)
        setup_, result_ = rest.split(": ", 1)
        return any(esc(check_) in ln and esc(setup_) in ln and esc(result_) in ln
                   for ln in re.split(r"\n|</li>", text))
    check(md_null and "Tested alternative setup:" not in md_null and all(on_page(md_null, x) for x in links + masters),
          f"{label}: report.md's no-problem section carries the identified lines and no bare shared label")
    check(html_null and "Tested alternative setup:" not in html_null
          and all(on_page(html_null, x, html_escape) for x in links + masters),
          f"{label}: report.html's no-problem section carries the identified lines and no bare shared label")
    contact = [str(x) for x in reader.get("comparisonSummary") or []
               if str(x).startswith("Objects left without their supporting surface — ")]
    expect(len(contact), 2, f"{label}: the contact comparison has one no-additional-problem line per arm")
    check(not any("— Tested alternative setup:" in x for x in contact)
          and any(names(x, "candidate-02-patchb") for x in contact)
          and any(names(x, "candidate-03-combined") for x in contact),
          f"{label}: each no-additional-problem comparison line names its own arm's change (got {contact!r})")
    found = [f for f in plan.get("findings") or [] if f.get("kind") == "finding"
             and f.get("checkId") == "loadability.dangling-references"]
    check(len(found) == 1 and names(str(found[0].get("readerArmLabel")), "candidate-01-patcha"),
          f"{label}: the record-link finding names the arm it was found on "
          f"(got {[f.get('readerArmLabel') for f in found]!r})")
    check("**Tested alternative setup**\n" not in md and "<h3>Tested alternative setup</h3>" not in html,
          f"{label}: the per-setup load-order headings do not repeat the bare shared label")

    # two alternatives with the same label AND the same change: followup_identical_label_control
    check("setups with this change" not in md and "setups with no plugin change" not in md
          and not any(a.get("setupOrdinal") for a in (plan.get("scope") or {}).get("arms") or []),
          f"{label}: alternatives told apart by their changes carry no setup number")

    # a single alternative is the only setup with its label: it keeps the bare label everywhere
    plan, md, html = patch_direction_control(bundle, "removes")
    single = [str(x) for x in (plan.get("reader") or {}).get("nullSummary") or [] if " — Tested alternative setup" in str(x)]
    check(single and all("— Tested alternative setup: " in x for x in single) and "**Tested alternative setup**\n" in md,
          f"{label}/single: one removal alternative keeps the bare label in its lines and heading (got {single!r})")


def html_escape(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&#34;").replace("'", "&#39;")


def followup_title_control(bundle: Path):
    """Known answer: with no separate patch plugin the title is not a patch review and does not name the target's
    incidental conflict partners as the comparison; it names the target within the measured load order. A title
    with a patch keeps naming the patch."""
    label = "R4/followup-title"
    plan, md, html = patch_direction_control(bundle, "none")
    reader = plan.get("reader") or {}
    want = "FixtureTarget.esp within the measured load order — conflict review"
    expect(reader.get("title"), want, f"{label}/none: the title with no separate patch")
    check(md.startswith(f"# {want}") and f"<title>{want}</title>" in html and f"<h1>{want}</h1>" in html,
          f"{label}/none: report.md and report.html carry that title")
    mods = [str(m) for m in reader.get("comparisonMods") or []]
    check("patch review" not in str(reader.get("title")) and not any(m in str(reader.get("title")) for m in mods),
          f"{label}/none: no 'patch review' and no conflict partner in the title (mods {mods!r})")
    check("No separate patch plugin was declared for this comparison." in str(reader.get("scopeLine")),
          f"{label}/none: the opening still says no separate patch was compared")
    plan, md, html = patch_direction_control(bundle, "removes")
    expect((plan.get("reader") or {}).get("title"), "FixtureTarget.esp with Patch.esp — patch review",
           f"{label}/removes: a compared patch keeps the patch-review title")


NAVMESH_CHECK = "geometry.navmesh-ownership"


def followup_totals_control(bundle: Path, variant: str):
    """The totals beside a no-additional-problem comparison. Built in memory from R4's bundle with synthetic probe
    emits for one check, in the shape a real navmesh probe run produced: the probe's conflict list for the measured
    load order holds broken-link rows (EDGE-IDX) AND an overwrite-history row (CLOBBERED), so the probe reports
    4 conflicts while the report counts 3 problems. Variants:

      navmesh  baseline: EDGE-IDX FixtureBase|FixtureOther x2, EDGE-IDX FixtureBase|FixtureTarget x1,
               CLOBBERED FixtureBase|FixtureOther x1 (4 rows); the alternative removes FixtureTarget.esp and keeps
               EDGE-IDX FixtureBase|FixtureOther x2 + CLOBBERED x1 (3 rows)
      plain    only the target's EDGE-IDX row at baseline, nothing on the alternative (no other pair, no history)
      patch    as navmesh, but the measured order also loads Patch.esp and the alternative removes Patch.esp
      overlap  geometry.object-overlap: OVERLAP FixtureBase|FixtureOther x2 at baseline, x1 on the alternative

    Returns (plan, report.md, report.html, comparison line)."""
    spec = importlib.util.spec_from_file_location(f"evalreport_followup_totals_{variant}", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    view = module.Bundle(bundle)
    view.disposition = copy.deepcopy(view.disposition)
    view.plan = copy.deepcopy(view.plan)
    wanted = {"baseline", "candidate-02-patchb"}
    view.disposition["arms"] = {k: v for k, v in view.disposition["arms"].items() if k in wanted}
    view.plan["arms"] = [a for a in view.plan.get("arms") or [] if a.get("armId") in wanted]
    arms = {a["armId"]: a for a in view.plan["arms"]}
    base = list(arms["baseline"]["requested"]["plugins"])
    patch = copy.deepcopy(base[-1])
    patch["name"] = "Patch.esp"
    if variant == "patch":
        orders = {"baseline": base + [patch], "candidate-02-patchb": base}
        view.plan["patches"] = ["Patch.esp"]      # declared, never inferred (636ae180)
    else:
        orders = {"baseline": base, "candidate-02-patchb": [p for p in base if p.get("name") != "FixtureTarget.esp"]}
    for arm_id, plugins in orders.items():
        arms[arm_id]["requested"]["plugins"] = plugins
        arms[arm_id]["kind"] = "baseline" if arm_id == "baseline" else "candidate"
        view.disposition["arms"][arm_id]["kind"] = arms[arm_id]["kind"]
        view.disposition["arms"][arm_id].pop("orderEvaluation", None)
    check_id = "geometry.object-overlap" if variant == "overlap" else NAVMESH_CHECK

    def conflict(kind, a, b, n, tag):
        return {"Kind": kind, "A": a, "B": b, "Summary": f"synthetic {kind} {tag}",
                "Necessary": [{"FormKey": f"{n:06X}:FixtureBase.esp", "Label": f"FixtureCell{n}", "Physical": "Holds"}]}

    other = ("FixtureBase.esp", "FixtureOther.esp")
    if variant == "overlap":
        emits = {"baseline": [conflict("OVERLAP", *other, 0x800 + i, "base") for i in range(2)],
                 "candidate-02-patchb": [conflict("OVERLAP", *other, 0x800, "alt")]}
    elif variant == "plain":
        emits = {"baseline": [conflict("EDGE-IDX", "FixtureBase.esp", "FixtureTarget.esp", 0x900, "base")],
                 "candidate-02-patchb": []}
    else:
        history = conflict("CLOBBERED", *other, 0xA00, "history")
        emits = {"baseline": [conflict("EDGE-IDX", *other, 0xB00 + i, "base") for i in range(2)]
                             + [conflict("EDGE-IDX", "FixtureBase.esp", "FixtureTarget.esp", 0xC00, "base"), history],
                 "candidate-02-patchb": [conflict("EDGE-IDX", *other, 0xB00 + i, "alt") for i in range(2)] + [history]}
    paths = {arm_id: f"synthetic/{check_id}/{arm_id}.json" for arm_id in emits}
    original_emit, original_path = view.emit, view.output_emit_path
    view.emit = lambda p: ({"Conflicts": emits[next(k for k, v in paths.items() if v == p)], "Coverage": []}
                           if p in paths.values() else original_emit(p))
    view.output_emit_path = lambda c, a, part: (paths.get(a) if c == check_id and part == "arm" else original_path(c, a, part))
    evaluation = view.disposition["evaluation"]
    for key in ("missing", "findings", "completedNullChecks", "completedCatalogues"):
        evaluation[key] = [r for r in evaluation.get(key) or [] if r.get("armId") in wanted and r.get("checkId") != check_id]
    evaluation["findings"] += [{"checkId": check_id, "armId": arm_id, "part": "arm", "findings": len(rows),
                                "verdict": None, "strength": "calibrated-inference"}
                               for arm_id, rows in emits.items() if rows]
    evaluation["completedNullChecks"].append({"checkId": check_id, "armId": "candidate-02-patchb", "part": "diff", "coverage": 0})
    priorities = module.load_json(module.PRIORITIES)
    concise = priorities.get("concise") or {}
    page = priorities.get("page") or {}
    settings = {"mode": "concise", "layout": concise.get("layout", "normal"), "maxPages": 8,
                "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                         "baseFontPt": page.get("baseFontPt", 10.5)},
                "rowsPerGroup": concise.get("rowsPerGroup", 3),
                "figuresPerFinding": concise.get("figuresPerFinding", 1),
                "reel": priorities.get("reel") or {}}
    original_bundle = module.Bundle
    try:
        module.Bundle = lambda _: view
        plan = module.compile_plan(bundle, {"bundle": {"complete": True},
                                            "evaluation": {"complete": False, "claim": "incomplete"}},
                                   settings, priorities, str(bundle))
    finally:
        module.Bundle = original_bundle
    shown = module.effective_view(plan, settings["rowsPerGroup"], settings["figuresPerFinding"], priorities,
                                  settings["layout"])
    md = module.env(False).get_template("report.md.j2").render(p=shown, css="")
    html = module.env(True).get_template("report.html.j2").render(p=shown, css="")
    title = ((priorities.get("reader") or {}).get("checkTitles") or {}).get(check_id, check_id)
    line = next((str(x) for x in (plan.get("reader") or {}).get("comparisonSummary") or []
                 if str(x).startswith(f"{title} — ")), "")
    return plan, md, html, line


def followup_totals_wording_control(bundle: Path):
    """Known answers for a 'probe says 4, report says 3' reading of a navmesh comparison. The probe's conflict list for
    a setup counts every row it emits, overwrite history included (the evidence index's "out of N conflict(s)"
    is that number); the report's totals count only rows reported as problems, across every plugin pair the check
    reported. Neither number is wrong. The sentence must name what it counts, that it includes pairs the report does
    not list, and the overwrite history it leaves out, so the two numbers cannot read as a contradiction."""
    label = "R4/followup-totals"
    plan, md, html, line = followup_totals_control(bundle, "navmesh")
    base_rows = [f for f in plan.get("findings") or [] if f.get("kind") == "finding"
                 and f.get("checkId") == NAVMESH_CHECK and f.get("armId") == "baseline"]
    probe_total = {int((f.get("counts") or {}).get("findingsInPart") or 0) for f in base_rows}
    counted = sum(int(f.get("rowsTotal") or 0) for f in base_rows if not f.get("readerEvidenceReason"))
    history = sum(int(f.get("rowsTotal") or 0) for f in base_rows if f.get("readerEvidenceReason") == "context-only-kind")
    check(probe_total == {4} and counted == 3 and history == 1 and counted + history == 4,
          f"{label}: the fixture reproduces the shape: the probe reports 4, 3 are problems, 1 is overwrite history "
          f"(got probe {probe_total!r}, counted {counted}, history {history})")
    check("out of 4 conflict(s) the probe reported" in " ".join(str(f.get("plain")) for f in base_rows),
          f"{label}: the evidence-index sentence keeps the probe's own total of 4")
    # round 5 (third blind review, W-S4): the navmesh rows are counted in their own unit, broken links
    want = ("In total this check reported 3 broken navmesh links in the measured load order and 2 in this setup, "
            "including conflicts that do not involve the target mods, which this report does not list.")
    check(want in line, f"{label}/navmesh: the totals name their unit and the unlisted pairs (got {line!r})")
    want_history = ("Not counted: 1 navmesh edit overwritten by another mod in the measured load order and 1 in this "
                    "setup, kept in the evidence index as history rather than as problems.")
    check(want_history in line, f"{label}/navmesh: the totals name the overwrite history they leave out (got {line!r})")
    check("The measured totals are" not in line and " before and " not in line,
          f"{label}/navmesh: no unqualified 'measured totals ... before and ...' sentence")
    check(want in md and want_history in md and html_escape(want) in html and html_escape(want_history) in html,
          f"{label}/navmesh: report.md and report.html carry both sentences")
    plan, md, html, line = followup_totals_control(bundle, "plain")
    check(line.endswith("In total this check reported 1 broken navmesh link in the measured load order and 0 in this setup.")
          and "Not counted" not in line and "do not involve" not in line,
          f"{label}/plain: with no unlisted pair and no history, neither clause appears (got {line!r})")
    plan, md, html, line = followup_totals_control(bundle, "patch")
    check("including conflicts that do not involve the target mods or a compared patch, which this report does not list."
          in line, f"{label}/patch: with a compared patch the unlisted pairs involve neither (got {line!r})")
    plan, md, html, line = followup_totals_control(bundle, "overlap")
    check("In total this check reported 2 overlap pairs in the measured load order and 1 in this setup" in line
          and "records with a problem" not in line,
          f"{label}/overlap: an overlap check counts overlap pairs, the unit its finding sentences use (got {line!r})")


def followup_identical_label_control(bundle: Path):
    """Known answer: two alternatives with the same label AND the same change are told apart in reader words - their
    position among the setups making that change, which the setups-compared row states too - never by setup id."""
    label = "R4/followup-identical"
    plan, md, html = alternative_identity_control(bundle, identical=True)
    arms = {a.get("armId"): a for a in (plan.get("scope") or {}).get("arms") or []}
    want = {"candidate-02-patchb": "Tested alternative setup (removes FixtureSecond.esp; 1 of 2 setups with this change)",
            "candidate-03-combined": "Tested alternative setup (removes FixtureSecond.esp; 2 of 2 setups with this change)"}
    for arm_id, reader_label in want.items():
        expect(arms.get(arm_id, {}).get("readerLabel"), reader_label, f"{label}: {arm_id}'s reader label")
    links = [str(x) for x in (plan.get("reader") or {}).get("nullSummary") or []
             if str(x).startswith("Missing or broken record links — ")]
    check(sorted(links) == sorted(f"Missing or broken record links — {x}: This completed check found no problem in the "
                                  "records it examined" for x in want.values()),
          f"{label}: one no-problem line per identical arm, each with its own number (got {links!r})")
    for n in (1, 2):
        row = f"removes FixtureSecond.esp ({n} of 2 setups with this change)."
        check(f"- **Tested alternative setup**: {row}" in md and f"<strong>Tested alternative setup</strong>: {row}" in html,
              f"{label}: the setups-compared row states '{n} of 2 setups with this change' in report.md and report.html")
    leaked = [s for s in ("candidate-02-patchb", "candidate-03-combined", "setup candidate-")
              if s in md or s in html or any(s in x for x in links)]
    check(not leaked, f"{label}: no technical setup id in report.md, report.html or the no-problem lines (got {leaked!r})")
    plan, md, html = alternative_identity_control(bundle, identical="nochange")
    arms = {a.get("armId"): a for a in (plan.get("scope") or {}).get("arms") or []}
    expect([arms.get(x, {}).get("readerLabel") for x in ("candidate-02-patchb", "candidate-03-combined")],
           ["Tested alternative setup (1 of 2 setups with no plugin change)",
            "Tested alternative setup (2 of 2 setups with no plugin change)"],
           f"{label}/nochange: two alternatives with no plugin change are numbered too")
    check("- **Tested alternative setup** (2 of 2 setups with no plugin change)." in md
          and "candidate-03-combined" not in md,
          f"{label}/nochange: the setups-compared row carries the number and no setup id")


# ------------------------------------------------- what still needs checking: which setup is it missing on

def missing_setup_control(bundle: Path):
    """Two setups, and a missing list with the asymmetry the real end-user bundle has (2026-09-17): one check
    `mcp-only` on BOTH setups, one `mcp-only` on the alternative ONLY (accepted on the measured load order), and
    one `skipped-user` on the measured load order ONLY. Built in memory from R4's bundle the way the overlap
    wording control is. Returns (plan, report.md, report.html)."""
    spec = importlib.util.spec_from_file_location("evalreport_missing_setup", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    view = module.Bundle(bundle)
    view.disposition = copy.deepcopy(view.disposition)
    view.plan = copy.deepcopy(view.plan)
    wanted = {"baseline", "candidate-02-patchb"}
    view.disposition["arms"] = {k: v for k, v in view.disposition["arms"].items() if k in wanted}
    view.plan["arms"] = [a for a in view.plan.get("arms") or [] if a.get("armId") in wanted]
    evaluation = view.disposition["evaluation"]
    for key in ("missing", "findings", "completedNullChecks", "completedCatalogues"):
        evaluation[key] = [row for row in evaluation.get(key) or [] if row.get("armId") in wanted]
    used = {"loadability.dangling-references", "loadability.masters-and-order", "geometry.route-cut"}
    for key in ("missing", "findings", "completedNullChecks", "completedCatalogues"):
        evaluation[key] = [row for row in evaluation.get(key) or [] if row.get("checkId") not in used]
    evaluation["missing"] = (evaluation["missing"] +
                             [{"checkId": "loadability.dangling-references", "armId": arm, "part": None,
                               "status": "mcp-only", "cause": "mcp-only", "required": True} for arm in sorted(wanted)] +
                             [{"checkId": "loadability.masters-and-order", "armId": "candidate-02-patchb", "part": None,
                               "status": "mcp-only", "cause": "mcp-only", "required": True},
                              {"checkId": "geometry.route-cut", "armId": "baseline", "part": None,
                               "status": "skipped-user", "cause": "skipped-user", "required": True}])
    evaluation["completedNullChecks"] = evaluation["completedNullChecks"] + [
        {"checkId": "loadability.masters-and-order", "armId": "baseline", "part": None, "coverage": 0}]
    priorities = module.load_json(module.PRIORITIES)
    concise = priorities.get("concise") or {}
    page = priorities.get("page") or {}
    settings = {"mode": "concise", "layout": concise.get("layout", "normal"), "maxPages": 8,
                "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                         "baseFontPt": page.get("baseFontPt", 10.5)},
                "rowsPerGroup": concise.get("rowsPerGroup", 3),
                "figuresPerFinding": concise.get("figuresPerFinding", 1),
                "reel": priorities.get("reel") or {}}
    original_bundle = module.Bundle
    try:
        module.Bundle = lambda _: view
        plan = module.compile_plan(bundle, {"bundle": {"complete": True},
                                            "evaluation": {"complete": False, "claim": "incomplete"}},
                                   settings, priorities, str(bundle))
    finally:
        module.Bundle = original_bundle
    shown = module.effective_view(plan, settings["rowsPerGroup"], settings["figuresPerFinding"], priorities,
                                  settings["layout"])
    md = module.env(False).get_template("report.md.j2").render(p=shown, css="")
    html = module.env(True).get_template("report.html.j2").render(p=shown, css="")
    return plan, md, html


def check_no_findings_headline(bundle: Path):
    """The reader's verdict for a COMPLETE evaluation whose claim is `no-findings-in-completed-checks`, which no
    fixture bundle carries (every one is incomplete). Built in memory from R4's bundle with the measured load order
    only, nothing missing and no findings. The wrong answer is the sentence for a result with no claim behind it."""
    label = "R4/no-findings-headline"
    spec = importlib.util.spec_from_file_location("evalreport_no_findings", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    view = module.Bundle(bundle)
    view.disposition = copy.deepcopy(view.disposition)
    view.plan = copy.deepcopy(view.plan)
    wanted = {"baseline"}
    view.disposition["arms"] = {k: v for k, v in view.disposition["arms"].items() if k in wanted}
    view.plan["arms"] = [a for a in view.plan.get("arms") or [] if a.get("armId") in wanted]
    evaluation = view.disposition["evaluation"]
    evaluation["missing"], evaluation["findings"] = [], []
    for key in ("completedNullChecks", "completedCatalogues"):
        evaluation[key] = [row for row in evaluation.get(key) or [] if row.get("armId") in wanted]
    evaluation["complete"], evaluation["claim"] = True, "no-findings-in-completed-checks"
    priorities = module.load_json(module.PRIORITIES)
    concise = priorities.get("concise") or {}
    page = priorities.get("page") or {}
    settings = {"mode": "concise", "layout": concise.get("layout", "normal"), "maxPages": 8,
                "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                         "baseFontPt": page.get("baseFontPt", 10.5)},
                "rowsPerGroup": concise.get("rowsPerGroup", 3),
                "figuresPerFinding": concise.get("figuresPerFinding", 1),
                "reel": priorities.get("reel") or {}}
    original_bundle = module.Bundle
    try:
        module.Bundle = lambda _: view
        plan = module.compile_plan(bundle, {"bundle": {"complete": True},
                                            "evaluation": {"complete": True, "claim": "no-findings-in-completed-checks"}},
                                   settings, priorities, str(bundle))
    finally:
        module.Bundle = original_bundle
    reader = plan.get("reader") or {}
    verdict = str(headline(reader) or reader.get("verdict") or "")
    expect(verdict, "The completed checks found no problem in what they examined.",
           f"{label}: a complete evaluation with no findings says so, in the claim's own words")


def check_missing_setup_naming(bundle: Path):
    """Known answers for "What still needs checking" (the retained 2026-09-17 defect): the disposition's missing
    list is per arm, so every not-supplied line names the setup it is missing ON. A check supplied on one setup
    and missing on another must name only the setup it is missing on - the RED control is the merged wording,
    which named no setup and spoke for the evaluation as a whole."""
    label = "R4/missing-setup"
    plan, md, html = missing_setup_control(bundle)
    reader = plan.get("reader") or {}
    arms = {a.get("armId"): a for a in (plan.get("scope") or {}).get("arms") or []}
    base_label = arms.get("baseline", {}).get("readerLabel")
    alt_label = arms.get("candidate-02-patchb", {}).get("readerLabel")
    titles = reader.get("checkTitles") or {}
    both = titles.get("loadability.dangling-references")
    alt_only = titles.get("loadability.masters-and-order")
    base_only = titles.get("geometry.route-cut")
    check(bool(base_label and alt_label and both and alt_only and base_only),
          f"{label}: the control names both setups and all three checks "
          f"(got {base_label!r}, {alt_label!r}, {both!r}, {alt_only!r}, {base_only!r})")
    # the rows stay grouped by status (the reason is a statement about the status, and one check can be missing
    # under two different statuses); each SUBJECT names the setup it is missing on
    by_reason = {str(r.get("reason")): list(r.get("subjects") or []) for r in reader.get("missingSummary") or []}
    not_supplied = "is available through houseCARL, but its result was not supplied for this evaluation"
    left_out = "was left out of this run"
    expect(sorted(x for x in by_reason.get(not_supplied, []) if x.split(" — ")[0] in (both, alt_only)),
           sorted([f"{both} — {base_label}", f"{both} — {alt_label}", f"{alt_only} — {alt_label}"]),
           f"{label}: the not-supplied group names the setup per check, and only the setups it is missing on")
    expect([x for x in by_reason.get(left_out, []) if x.split(" — ")[0] == base_only],
           [f"{base_only} — {base_label}"],
           f"{label}: a check left out on one setup only names that setup")
    check(f"{alt_only} — {base_label}" not in by_reason.get(not_supplied, []),
          f"{label}: the setup that supplied {alt_only!r} is not listed as missing it")
    # scoped to the section itself: the same "check — setup" phrase legitimately appears in the no-problem list
    # for the setup that DID supply the result, which is the point.
    # Since the window-2 report review (P53, accepted under the standing rule 2026-10-17) the concise page lists here
    # only the missing checks on setups the coverage section ("What the checks could not judge") does not already
    # list by reason and setup; one sentence points there. The plan's missingSummary stays complete (asserted above).
    covered = set(((reader.get("coverage") or {}).get("relevantArms")) or [])
    label_of = {base_label: "baseline", alt_label: "candidate-02-patchb"}
    for fmt, text_, start, end in (("report.md", md, "## What still needs checking", "\n## "),
                                   ("report.html", html, "<h2>What still needs checking</h2>", "<h2>")):
        esc = html_escape if fmt == "report.html" else (lambda s: s)
        body = text_.split(start, 1)[1]
        body = body.split(end, 1)[0]
        check(bool(body.strip()), f"{label}/{fmt}: the what-still-needs-checking section was located")
        check(not covered or "listed under" in body,
              f"{label}/{fmt}: the section points to the coverage section for the setups it covers")
        for title, setup in ((both, base_label), (both, alt_label), (alt_only, alt_label), (base_only, base_label)):
            on_page = esc(f"{title} — {setup}") in body
            if title == both:
                # (Missives r3 report review, finding 3) missing on every setup: said once, in the coverage section,
                # "on every tested setup", never again here for the setups that section does not list
                check(not on_page, f"{label}/{fmt}: '{title} — {setup}' is not repeated (missing on every setup)")
            elif label_of.get(setup) in covered:
                check(not on_page, f"{label}/{fmt}: '{title} — {setup}' is not repeated (the coverage section lists it)")
            else:
                check(on_page, f"{label}/{fmt}: '{title} — {setup}' is named in what still needs checking")
        for title, setup in ((alt_only, base_label), (base_only, alt_label)):
            check(esc(f"{title} — {setup}") not in body,
                  f"{label}/{fmt}: '{title} — {setup}' is NOT named there - that setup is not missing that check")
        # the reason clause stays the group's lead, said once per status, with its explanation attached to it
        check(body.count(not_supplied[:1].upper() + not_supplied[1:]) == 1,
              f"{label}/{fmt}: the not-supplied reason is stated once, as the group's lead")
    # what the page no longer repeats is on it once, in the coverage section, with its setup
    cov = md.split("## What the checks could not judge", 1)[1].split("\n## ", 1)[0]
    for title in (both, base_only):
        check(title in cov, f"{label}: '{title}' is named in the coverage section")
    explanation = "These checks still need to be completed for each setup being considered."
    expect([md.count(explanation), html.count(explanation)], [1, 1],
           f"{label}: the status explanation stays attached to its own group, once")


# ---------------------------------------------------------------- imported evidence: where it came from

IMPORT_ORIGIN_CASE = "R7-import-origin"


def import_origin_view(bundle: Path, drop_disposition_list: bool, strip_receipts: bool, same_evaluation=None,
                       with_index=False):
    """The same bundle read with less (or differently declared) origin information, compiled in memory (the
    patch-direction control's method).

      drop_disposition_list   a bundle finalized before the disposition listed its imports: origin is derived from
                              the accepted receipts
      strip_receipts          no origin anywhere: receipts in the shape they had before an import declared one
      same_evaluation         an arm id whose reused import declares its source INSIDE this evaluation
                              (`reusedWithin: this-evaluation`, source plan = this plan), which the reader must be
                              told differently from reuse across evaluations

    Returns (plan, report.md, report.html)."""
    spec = importlib.util.spec_from_file_location(
        f"evalreport_import_origin_{drop_disposition_list}_{strip_receipts}", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if str(bundle) in AI_OF:      # joined with its overlay, as the on-disk compile (plan --ai-bundle) reads it
        module.AI_BUNDLES[str(Path(bundle).resolve())] = Path(AI_OF[str(bundle)]).resolve()
    view = module.Bundle(bundle)
    view.disposition = copy.deepcopy(view.disposition)
    if drop_disposition_list:
        view.disposition.pop("importedEvidence", None)
    if strip_receipts:
        view.receipts = copy.deepcopy(view.receipts)
        for rc in view.receipts.values():
            imported = rc.get("imported")
            if isinstance(imported, dict):
                for key in ("origin", "originCheck", "reusedFrom", "reusedFromVerified", "reusedFromNote"):
                    imported.pop(key, None)
        view.receipt_by_key = {(rc.get("checkId"), rc.get("armId"), rc.get("part")): rc
                               for rc in view.receipts.values()}
    if same_evaluation:
        view.receipts = copy.deepcopy(view.receipts)
        this_plan = (view.plan or {}).get("planId")
        for rc in view.receipts.values():
            imported = rc.get("imported")
            if rc.get("armId") == same_evaluation and isinstance(imported, dict) and imported.get("reusedFrom"):
                imported["reusedWithin"] = "this-evaluation"
                imported["reusedFrom"] = dict(imported["reusedFrom"], planId=this_plan)
        view.receipt_by_key = {(rc.get("checkId"), rc.get("armId"), rc.get("part")): rc
                               for rc in view.receipts.values()}
        view.disposition = copy.deepcopy(view.disposition)
        for row in view.disposition.get("importedEvidence") or []:
            if row.get("armId") == same_evaluation:
                row["reusedWithin"] = "this-evaluation"
    priorities = module.load_json(module.PRIORITIES)
    concise = priorities.get("concise") or {}
    page = priorities.get("page") or {}
    settings = {"mode": "concise", "layout": concise.get("layout", "normal"), "maxPages": 8,
                "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                         "baseFontPt": page.get("baseFontPt", 10.5)},
                "rowsPerGroup": concise.get("rowsPerGroup", 3),
                "figuresPerFinding": concise.get("figuresPerFinding", 1),
                "reel": priorities.get("reel") or {}}
    original = module.Bundle
    try:
        module.Bundle = lambda _: view
        plan = module.compile_plan(bundle, {"bundle": {"complete": True},
                                            "evaluation": {"complete": False, "claim": "incomplete"}},
                                   settings, priorities, str(bundle))
    finally:
        module.Bundle = original
    shown = module.effective_view(plan, settings["rowsPerGroup"], settings["figuresPerFinding"], priorities,
                                  settings["layout"])
    md = module.env(False).get_template("report.md.j2").render(p=shown, css="")
    html = module.env(True).get_template("report.html.j2").render(p=shown, css="")
    if with_index:
        return plan, md, html, module.env(False).get_template("evidence-index.md.j2").render(p=shown, css="")
    return plan, md, html


def import_origin_skeleton(plan: dict) -> list:
    """Everything a measured count is read from, with no origin information in it: one row per finding with its
    identity, its counts and its row total, plus the completion block and the summary lengths."""
    rows = [(f.get("id"), f.get("kind"), f.get("checkId"), f.get("armId"), f.get("part"), f.get("rowsTotal"),
             json.dumps(f.get("counts"), sort_keys=True)) for f in plan.get("findings") or []]
    return [sorted(rows), (plan.get("scope") or {}).get("completion"), len(plan.get("evidence") or []),
            len((plan.get("reader") or {}).get("nullSummary") or []),
            len((plan.get("reader") or {}).get("missingSummary") or [])]


def all_reader_lines(pres: dict) -> list:
    """Every reader-facing sentence that names one setup: the no-problem lines, the comparison and catalogue lines,
    and each relevant finding's own sentence with its qualification."""
    reader = pres.get("reader") or {}
    titles = reader.get("checkTitles") or {}
    lines = [str(x) for x in (reader.get("nullSummary") or []) + (reader.get("comparisonSummary") or [])
             + (reader.get("catalogueSummary") or [])]
    for f in pres.get("findings") or []:
        if f.get("readerRelevant") and f.get("kind") in ("regression", "finding", "repaired", "reclassified",
                                                         "null", "catalogue"):
            title = titles.get(str(f.get("checkId")), f.get("title"))
            lines.append(f"{title} - {f.get('readerArmLabel')}: {f.get('readerPlain')} "
                         f"{f.get('readerProvenance') or ''}".strip())
    return lines


def import_origin_control(tmp: Path, index: dict):
    """Known answers for how a report describes imported MCP evidence (option A, 2026-09-17). The fixture bundle is
    real: one mcp-only check imported by the controller fresh and reused from another evaluation, and one arm whose
    prerequisite failed. Beside the affected result the reader must be told that a reused result was not measured for
    this evaluation and that its source was declared rather than re-checked; that a result whose origin is not recorded
    is never called fresh (read here with the origin fields removed: the pre-origin receipt a bundle once carried had
    only the Python controller as its writer); and that an arm whose prerequisite failed is credited nowhere. Plan, arm and hash detail
    belongs to the evidence index, and no measured count may move because of any of it."""
    case = IMPORT_ORIGIN_CASE
    label = "R7/import-origin"
    if not cli_present(case):
        return
    fixture = index[case]
    bundle = Path(fixture["bundle"])
    arms = fixture["arms"]
    fresh_arm, reused_arm = arms["candidate-00-baseline"], arms["candidate-01-reorder"]
    legacy_arm, blocked_arm = arms["candidate-02-legacy"], arms["candidate-03-missing-master"]
    check_id, prerequisite = fixture["importCheck"], fixture["prerequisite"]
    out = tmp / case
    pres, _, _, v = compile_case(case, bundle, out)
    if pres is None:
        return
    md, html, idx = text(out / "report.md"), text(out / "report.html"), text(out / "evidence-index.md")
    expect(v.returncode, 0, f"{label}: premise - verify exits 0 on the report built from the import fixture", "D5")

    # ---- the premises this bundle was built for, read from the overlay bundle that holds the imports
    ai_bundle = Path(fixture["aiBundle"])
    disp = load(ai_bundle / "disposition.json") or {}
    ledger = load(ai_bundle / "ledger.json") or {}
    declared = {r.get("armId"): r.get("origin") for r in disp.get("importedEvidence") or []}
    expect([declared.get(fresh_arm), declared.get(reused_arm), declared.get(legacy_arm), declared.get(blocked_arm)],
           ["fresh", "reused", None, None],
           f"{label}: premise - the bundle credits a fresh and a reused import and no blocked one")
    expect(((ledger.get("entries") or {}).get(f"{check_id}__{fresh_arm}") or {}).get("status"), "reused",
           f"{label}: premise - the FRESH import's ledger status is the controller's own `reused`")

    # ---- the plan records each credited import's origin, from the receipt, and no uncredited one
    rows = {r.get("armId"): r for r in ((pres.get("imports") or {}).get("rows") or [])}
    expect([(pres.get("imports") or {}).get("source"), sorted(rows)],
           ["disposition", sorted([fresh_arm, reused_arm])],
           f"{label}: the plan lists the two credited imports and where the list came from")
    expect([rows.get(a, {}).get("origin") for a in (fresh_arm, reused_arm)],
           ["fresh", "reused"], f"{label}: each import's origin as the receipt records it")
    expect((rows.get(reused_arm) or {}).get("reusedFrom"), fixture["reusedFrom"],
           f"{label}: the reused import keeps the source plan, arm and result hash it declared")
    expect((rows.get(reused_arm) or {}).get("reusedFromVerified"), False,
           f"{label}: the declared source is never presented as verified")

    # ---- the reader sentences, beside the affected result, in report.md and report.html
    arm_labels = {a.get("armId"): a.get("readerLabel") for a in (pres.get("scope") or {}).get("arms") or []}
    reused_sentence = ("This result was not measured for this evaluation: it was reused from an earlier one, and "
                       "that source was stated by whoever supplied it rather than re-checked here.")
    unknown_sentence = "It is not recorded whether this result was captured for this evaluation."
    lines = all_reader_lines(pres)
    import_title = ((pres.get("reader") or {}).get("checkTitles") or {}).get(check_id, check_id)

    def line_for(arm_id: str) -> str:
        """The reader-facing line of the IMPORTED check on one setup, so a neighbouring check's line on the same
        setup cannot stand in for it."""
        return next((x for x in lines if arm_labels[arm_id] in x and import_title in x), "")

    reused_line, fresh_line = line_for(reused_arm), line_for(fresh_arm)
    check(reused_sentence.rstrip(".") in reused_line,
          f"{label}: the reused result's own line says it was not measured here and its source was not re-checked "
          f"(got {reused_line!r})")
    check(bool(fresh_line) and reused_sentence.rstrip(".") not in fresh_line
          and unknown_sentence.rstrip(".") not in fresh_line
          and "reused" not in fresh_line.lower(),
          f"{label}: a fresh result carries no reuse or unknown-origin qualification, and the controller's `reused` "
          f"ledger status never reaches the reader (got {fresh_line!r})")
    # Re-baselined 2026-10-09 (lane P, D1 accepted): the reused result reports a count with no pair of mods, so it
    # leaves the report's detail list for the evidence index's set-aside list, and its qualification travels with it
    # (said beside the result, where the result is printed).
    reused_ids = [f.get("id") for f in pres.get("findings") or [] if f.get("kind") == "finding"
                  and f.get("checkId") == check_id and f.get("armId") == reused_arm]
    aside_lines = [ln for ln in idx.splitlines() if ln.startswith("- [") and any(f"[{i}]" in ln for i in reused_ids)
                   and "naming no pair of mods" in ln]
    check(bool(reused_ids) and any(normalise(reused_sentence) in normalise(ln) for ln in aside_lines),
          f"{label}: the reused result's set-aside line in evidence-index.md carries its qualification "
          f"(ids {reused_ids}, lines {aside_lines[:1]!r})")
    check(reused_arm not in md and blocked_arm not in md, f"{label}: no setup id leaks into the report")
    for token in (fixture["reusedFrom"]["planId"], fixture["reusedFrom"]["resultSha256"],
                  str((rows.get(reused_arm) or {}).get("receiptId"))):
        check(token not in md and token not in html,
              f"{label}: the technical provenance {token[:12]!r} stays out of the reader report")

    # ---- reuse between two setups of THIS evaluation is a different statement, and gets its own sentence
    # (decided 2026-09-17). The same bundle read with that declaration must say "not measured for this SETUP",
    # not "not measured for this evaluation".
    same_words = ("This result was not measured for this setup: it was reused from another setup in this evaluation "
                  "whose tested configuration is the same, and that source was stated by whoever supplied it rather "
                  "than re-checked here.")
    same_plan, same_md, same_html, same_idx = import_origin_view(bundle, False, False, same_evaluation=reused_arm,
                                                                 with_index=True)
    same_lines = all_reader_lines(same_plan)
    same_line = next((x for x in same_lines if arm_labels[reused_arm] in x and import_title in x), "")
    check(same_words.rstrip(".") in same_line,
          f"{label}/within: the line says it was not measured for this SETUP and was reused from another setup of "
          f"this evaluation (got {same_line!r})")
    check(reused_sentence.rstrip(".") not in same_line,
          f"{label}/within: and no longer claims it came from an earlier evaluation (got {same_line!r})")
    # re-baselined 2026-10-09 (lane P, D1): the count-only reused result is listed in the evidence index's set-aside
    # list, and its qualification with it
    check(normalise(same_words) in normalise(same_idx) and normalise(same_words) not in normalise(same_md),
          f"{label}/within: the evidence index's set-aside line carries it, beside the result")
    # against the SAME in-memory path with only the declaration changed (comparing an in-memory plan with the
    # on-disk compile would compare two different code paths, which differ by one evidence row for the report itself)
    base_view, _, _ = import_origin_view(bundle, False, False)
    expect(import_origin_skeleton(same_plan), import_origin_skeleton(base_view),
           f"{label}/within: and no measured count moves with the wording", "counts")
    same_rows = {r["armId"]: r for r in ((same_plan.get("imports") or {}).get("rows") or [])}
    expect((same_rows.get(reused_arm) or {}).get("reusedWithin"), "this-evaluation",
           f"{label}/within: the plan records the reuse scope for the evidence index")

    # ---- the evidence index carries the technical provenance and states the limit once
    limit = ("These labels catch evidence filed under the wrong evaluation by mistake. They are not proof of where "
             "the evidence came from: the capture token and the timestamps are public and can be rewritten.")
    expect(normalise(idx).count(normalise(limit)), 1,
           f"{label}: the relabelling-not-authenticity limit is stated once, where imported evidence is described")
    for token in (fixture["reusedFrom"]["planId"], fixture["reusedFrom"]["armId"],
                  fixture["reusedFrom"]["resultSha256"]):
        check(token in idx, f"{label}: the evidence index carries the reused import's {token[:12]!r}")
    for arm in (fresh_arm, reused_arm):
        rid = str((rows.get(arm) or {}).get("receiptId"))
        check(rid in idx, f"{label}: the evidence index names the receipt of the import on {arm}")

    # ---- the arm whose prerequisite failed: credited nowhere (that the report names the prerequisite an accepted
    # import later waited for needed the Python controller run in process, D-A2, and is not tested here)
    check(blocked_arm not in rows and not line_for(blocked_arm),
          f"{label}: the blocked import is not credited and gets no result line "
          f"(got {line_for(blocked_arm)!r})")

    # ---- an older bundle: no disposition list, the same origins derived from the receipts
    derived, derived_md, _, derived_idx = import_origin_view(bundle, drop_disposition_list=True,
                                                             strip_receipts=False, with_index=True)
    derived_rows = {r.get("armId"): r.get("origin") for r in ((derived.get("imports") or {}).get("rows") or [])}
    expect([(derived.get("imports") or {}).get("source"),
            [derived_rows.get(a) for a in (fresh_arm, reused_arm)]],
           ["receipts", ["fresh", "reused"]],
           f"{label}: a bundle whose disposition lists no imports derives the same origins from its receipts")
    check(normalise(reused_sentence) in normalise(derived_idx),
          f"{label}: that older bundle's index carries the same qualification, where its result is")

    # ---- no measured count moves because of any of it
    stripped, stripped_md, _ = import_origin_view(bundle, drop_disposition_list=True, strip_receipts=True)
    stripped_rows = {r.get("armId"): r.get("origin") for r in ((stripped.get("imports") or {}).get("rows") or [])}
    expect([stripped_rows.get(a) for a in (fresh_arm, reused_arm)], ["unknown"] * 2,
           f"{label}: premise - with the origin fields removed every import reads unknown")
    check(normalise(reused_sentence) not in normalise(stripped_md),
          f"{label}: premise - and no result is then called reused")
    check(normalise(unknown_sentence) in normalise(stripped_md),
          f"{label}: and a result whose origin is not recorded says so beside it, never that it is fresh")
    with_origin, _, _ = import_origin_view(bundle, drop_disposition_list=False, strip_receipts=False)
    expect(import_origin_skeleton(with_origin), import_origin_skeleton(stripped),
           f"{label}: every finding identity, count, row total and completion number is identical to the same "
           f"bundle with no origin information")

    # ---- a plan made before imports were described is refused, not rendered silently
    stale = tmp / f"{case}-stale"
    shutil.copytree(out, stale)
    stale_plan = copy.deepcopy(pres)
    stale_plan.pop("imports", None)
    (stale / "presentation.json").write_text(json.dumps(stale_plan), encoding="utf-8")
    stale_res = evalreport(["report", "--presentation", stale / "presentation.json", "--out", stale / "out",
                            "--no-print"])
    check(stale_res.returncode == 2
          and "presentation-needs-replan" in (stale_res.stdout or "") + (stale_res.stderr or ""),
          f"{label}: a presentation planned before imported evidence was described is refused by name "
          f"(exit {stale_res.returncode})")


def case_r7(tmp, index, common):
    import_origin_control(tmp, index)

# ---------------------------------------------------------------------------- cases

def case_r1(tmp, index, common):
    case = "R1-intact-incomplete"
    o = load(EXPECTED / f"{case}.json")
    if not cli_present(case):
        return
    bundle = Path(index[case]["bundle"])
    out = tmp / case
    pres, p, r, v = compile_case(case, bundle, out)
    if pres is None:
        return
    md, _ = assert_common(case, bundle, out, pres, common, v) or ("", "")
    disp = load(bundle / "disposition.json") or {}
    sv = (pres.get("source") or {}).get("verify") or {}
    expect(sv, o["source"]["verify"], f"{case}: source.verify (bundle complete, evaluation incomplete)", "D1")
    by = ((pres.get("scope") or {}).get("completion") or {}).get("byCause") or {}
    for k, n in o["scope"]["completion"]["byCauseIncludes"].items():
        expect(by.get(k), n, f"{case}: byCause[{k}]", "D2")
    check(((pres.get("scope") or {}).get("completion") or {}).get("missingRequired", 0) >= o["scope"]["completion"]["missingRequiredAtLeast"],
          f"{case}: missingRequired >= {o['scope']['completion']['missingRequiredAtLeast']}")
    kinds = {f.get("kind") for f in pres.get("findings") or []}
    for k in o["findings"]["kindPresent"]:
        check(k in kinds, f"{case}: a finding of kind {k} exists")
    for k in o["findings"]["kindAbsent"]:
        check(k not in kinds, f"{case}: no finding of kind {k}")
    req = required_missing(disp)
    for m in req:
        hit = finding_where(pres, kind="missing", checkId=m["checkId"], armId=m["armId"])
        check(bool(hit), f"{case}: a `missing` finding for {m['checkId']} [{m['armId']}] (D3 (2))")
    verdicts = {f.get("verdict") for f in pres.get("findings") or []}
    for w in o["findings"]["verdictsVerbatimInclude"]:
        check(w in verdicts, f"{case}: the verdict {w} is retained verbatim on a finding (D3 (3))")
    ck = {x.get("kind") for x in pres.get("chapters") or []}
    for k in o["chapters"]["kindsPresent"]:
        check(k in ck, f"{case}: a chapter of kind {k} (D6)")
    caps = chapter_captions(pres, ("missing", "closing"))
    absent = sorted({m["checkId"] for m in req if m["checkId"] not in caps})
    expect(absent, [], f"{case}: the missing/closing chapters' captions name every required missing checkId", "D6")
    for s in o["report"]["mdContains"]:
        check(s in md, f"{case}: report.md contains {s!r}")
    low = md.lower()
    check("evaluation complete" not in low and "evaluation.complete: true" not in low,
          f"{case}: report.md never says the evaluation is complete")
    pm = load(out / "presentation-manifest.json") or {}
    expect((pm.get("print") or {}).get("status"), o["report"]["print"]["status"], f"{case}: print.status", "D4")
    check(isinstance((pm.get("print") or {}).get("pages"), int) and pm["print"]["pages"] <= o["report"]["print"]["pagesAtMost"],
          f"{case}: print.pages <= {o['report']['print']['pagesAtMost']} (got {(pm.get('print') or {}).get('pages')!r})")


def case_r2(tmp, index, common):
    case = "R2-null-vs-missing"
    o = load(EXPECTED / f"{case}.json")
    if not cli_present(case):
        return
    bundle = Path(index[case]["bundle"])
    out = tmp / case
    pres, p, r, v = compile_case(case, bundle, out)
    if pres is None:
        return
    md, _ = assert_common(case, bundle, out, pres, common, v) or ("", "")
    by = ((pres.get("scope") or {}).get("completion") or {}).get("byCause") or {}
    for k, n in o["scope"]["completion"]["byCauseIncludes"].items():
        expect(by.get(k), n, f"{case}: byCause[{k}]", "D2")
    n = o["nullFinding"]
    null = finding_where(pres, checkId=n["checkId"], armId=n["armId"], kind="null")
    null = [f for f in null if f.get("part") in (n["part"], None)]
    check(len(null) >= 1, f"{case}: a `null` finding for {n['checkId']} [{n['armId']}] (D3 (5))")
    if null:
        f = null[0]
        expect(f.get("rowsTotal"), n["rowsTotal"], f"{case}: null finding rowsTotal", "D2")
        check(n["countsContainNumber"] in numbers_in(f.get("counts")), f"{case}: null finding counts carry judged {n['countsContainNumber']} (got {f.get('counts')})")
        plain = str(f.get("plain") or "").lower()
        for s in n["plainDoesNotContain"]:
            check(s not in plain, f"{case}: null finding's sentence does not say {s!r}")
        null_plain = plain
        reader_plain = str(f.get("readerPlain") or "").lower()
        if f.get("part") == "diff":
            check("no additional problem" in reader_plain,
                  f"{case}: differential null says the comparison found no additional problem")
            check("reported separately" in reader_plain,
                  f"{case}: differential null preserves the possibility of existing setup problems")
    for m in o["missingFindings"]:
        hit = finding_where(pres, checkId=m["checkId"], armId=m["armId"], kind="missing")
        check(bool(hit), f"{case}: a `missing` finding for {m['checkId']} [{m['armId']}] (D3 (2))")
        if hit:
            expect(hit[0].get("rowsTotal"), m["rowsTotal"], f"{case}: missing finding {m['checkId']} rowsTotal", "D2")
            plain = str(hit[0].get("plain") or "").lower()
            for s in m["plainDoesNotContain"]:
                check(s not in plain, f"{case}: missing finding {m['checkId']}'s sentence does not say {s!r}")
            if null:
                check(plain != null_plain, f"{case}: the null and the missing sentences differ (they differ by kind, not by count)")
    kinds = {f.get("kind") for f in pres.get("findings") or []}
    for k in o["findings"]["kindAbsent"]:
        check(k not in kinds, f"{case}: no finding of kind {k}")
    for s in o["report"]["mdContains"]:
        check(s in md, f"{case}: report.md contains {s!r}")
    pm = load(out / "presentation-manifest.json") or {}
    expect((pm.get("print") or {}).get("status"), o["report"]["print"]["status"], f"{case}: print.status", "D4")
    check(isinstance((pm.get("print") or {}).get("pages"), int) and pm["print"]["pages"] <= o["report"]["print"]["pagesAtMost"],
          f"{case}: print.pages <= {o['report']['print']['pagesAtMost']} (got {(pm.get('print') or {}).get('pages')!r})")


def case_r3(tmp, index, common):
    case = "R3-many-findings-long-names"
    o = load(EXPECTED / f"{case}.json")
    if not cli_present(case):
        return
    bundle = Path(index[case]["bundle"])
    out = tmp / case
    pres, p, r, v = compile_case(case, bundle, out)
    if pres is None:
        return
    md, idx = assert_common(case, bundle, out, pres, common, v) or ("", "")
    disp = load(bundle / "disposition.json") or {}
    for k, name in o["longNames"].items():
        if k != "minimumLength":
            check(len(name) >= o["longNames"]["minimumLength"], f"{case}: fixture name {k} is >= 80 chars")
    groups = pres.get("groups") or []
    expect(len(groups), o["groups"]["count"], f"{case}: number of groups", "D3 grouping key")
    expect(sorted(g.get("key") for g in groups), sorted(o["groups"]["keys"]), f"{case}: group keys are Kind|A|B", "D2")
    expect([g.get("id") for g in groups], [f"G-{i + 1:03d}" for i in range(len(groups))], f"{case}: group ids G-001..", "D2")
    fmap = {f.get("id"): f for f in pres.get("findings") or []}
    for g in groups:
        kind = str(g.get("key") or "").split("|")[0]
        expect(g.get("checkId"), o["groups"]["checkIdByKind"].get(kind), f"{case}: {g.get('id')} checkId", "D3")
        check(len(g.get("findingIds") or []) >= o["groups"]["findingIdsAtLeastPerGroup"], f"{case}: {g.get('id')} has >= 2 findings")
        expect(g.get("rowsTotal"), sum(fmap.get(f, {}).get("rowsTotal", 0) for f in g.get("findingIds") or []),
               f"{case}: {g.get('id')} rowsTotal = sum of its findings", "D2")
    # complete counts per (checkId, armId, part)
    for row in (disp.get("evaluation") or {}).get("findings") or []:
        tot = sum(f.get("rowsTotal", 0) for f in finding_where(pres, kind="finding", checkId=row["checkId"], armId=row["armId"], part=row["part"]))
        expect(tot, row["findings"], f"{case}: sum rowsTotal for {row['checkId']} [{row['armId']}/{row['part']}]", "D2 rowsTotal")
    cs = o["concise"]
    abridged = 0
    for f in pres.get("findings") or []:
        if f.get("kind") != "finding":
            continue
        check(f.get("rowsShown", 0) <= cs["rowsPerGroup"], f"{case}: {f.get('id')} rowsShown <= {cs['rowsPerGroup']} (got {f.get('rowsShown')})")
        if f.get("rowsTotal", 0) > f.get("rowsShown", 0):
            abridged += 1
            om = [x for x in pres.get("omissions") or []
                  if str(x.get("where", "")).startswith(cs["omissionRowForEveryAbridgedFinding"]["whereStartsWith"])
                  and (f.get("id") in json.dumps(x) or (f.get("groupId") and f.get("groupId") in json.dumps(x)))]
            check(bool(om), f"{case}: an omissions row for abridged {f.get('id')} pointing into evidence-index.md")
    check(abridged >= cs["abridgedFindingsAtLeast"], f"{case}: at least {cs['abridgedFindingsAtLeast']} findings abridged (got {abridged})")
    seq = []
    for f in pres.get("findings") or []:
        if f.get("kind") == "finding":
            seq.append((f.get("checkId"), 0 if f.get("armId") == "baseline" else 1, -f.get("rowsTotal", 0)))
    check(seq == sorted(seq), f"{case}: findings sorted by checkId, baseline first, then count descending (D3)")
    clobber = [f for f in pres.get("findings") or []
               if (f.get("counts") or {}).get("groupKind") == "CLOBBERED"]
    check(bool(clobber) and all(f.get("readerRelevant") is False for f in clobber),
          f"{case}: bare navmesh overwrite groups remain indexed but are not reader compatibility findings")
    clobber_states = {r.get("physical") for f in clobber for r in f.get("allRows") or []}
    check({"Holds", "Gone"} <= clobber_states,
          f"{case}: both sampled-loss and no-sampled-loss overwrite controls are present and hidden")
    chapter_fids = {fid for c in pres.get("chapters") or [] if c.get("kind") == "finding" for fid in c.get("findingIds") or []}
    check(not chapter_fids.intersection({f.get("id") for f in clobber}),
          f"{case}: context-only overwrite groups produce no finding chapter")
    candidate_clobber = [f for f in clobber if f.get("armId") != "baseline" and f.get("part") != "reverse"]
    check(all(str(f.get("readerContext") or "").startswith("Already present before adding the patch") for f in candidate_clobber),
          f"{case}: exact baseline group/record matches are labeled pre-existing without claiming patch causation")
    check(all(f.get("id") in idx for f in clobber), f"{case}: every context-only overwrite remains in the evidence index")
    pm = load(out / "presentation-manifest.json") or {}
    expect((pm.get("print") or {}).get("status"), cs["print"]["status"], f"{case}: concise print.status", "D4 / acceptance")
    pages_c = (pm.get("print") or {}).get("pages")
    check(isinstance(pages_c, int) and pages_c <= cs["print"]["pagesAtMost"], f"{case}: concise pages <= 4 (got {pages_c!r})")
    for name in [v for k, v in o["longNames"].items() if k != "minimumLength"]:
        check(name in idx, f"{case}: evidence-index.md carries the full name {name[:30]}…")
        check(name in idx, f"{case}: evidence-index.md retains {name[:30]}…")
    emitted = set()
    for e in (load(bundle / "manifest.json") or {}).get("evidence") or []:
        # only JSON emits: a log row (or any non-.json path under the sweep folder) is text, not a document
        if e.get("kind") != "emit" or not str(e.get("path", "")).lower().endswith(".json"):
            continue
        try:
            doc = load(bundle / e["path"]) or {}
        except ValueError:
            continue
        if not isinstance(doc, dict):
            continue
        for c_ in doc.get("Conflicts") or []:
            for n in c_.get("Necessary") or []:
                emitted.add(n.get("FormKey"))
    absent = sorted(k for k in emitted if k and k not in idx)
    expect(absent, [], f"{case}: evidence-index.md names every emitted formKey", "D4 complete in both modes")
    # exhaustive
    ex = o["exhaustive"]
    out_x = tmp / f"{case}-exhaustive"
    pres_x, px, rx, vx = compile_case(case + "/exhaustive", bundle, out_x, exhaustive=True)
    if pres_x:
        expect((pres_x.get("settings") or {}).get("mode"), ex["mode"], f"{case}: --exhaustive sets settings.mode", "D2")
        bad = [f.get("id") for f in pres_x.get("findings") or [] if f.get("kind") == "finding" and f.get("rowsShown") != f.get("rowsTotal")]
        expect(bad, [], f"{case}: exhaustive lists every row (rowsShown == rowsTotal)", "D3")
        om = [x for x in pres_x.get("omissions") or [] if "abridg" in json.dumps(x).lower() or "rowsPerGroup" in json.dumps(x)]
        expect(om, [], f"{case}: exhaustive has no abridgement omissions", "D3")
        check(pres_x.get("presentationId") != pres.get("presentationId"), f"{case}: exhaustive presentationId differs from concise (D2)")
        pmx = load(out_x / "presentation-manifest.json") or {}
        pages_x = (pmx.get("print") or {}).get("pages")
        check(isinstance(pages_x, int), f"{case}: exhaustive records its page count (got {pages_x!r})")
        if isinstance(pages_x, int) and isinstance(pages_c, int):
            check(pages_x >= pages_c, f"{case}: exhaustive pages ({pages_x}) >= concise ({pages_c})")
        check(all(fid in text(out_x / "evidence-index.md") for fid in [f.get("id") for f in pres_x.get("findings") or []]),
              f"{case}: exhaustive evidence index retains every finding")


def case_r4(tmp, index, common):
    case = "R4-repaired-plus-regression"
    o = load(EXPECTED / f"{case}.json")
    if not cli_present(case):
        return
    bundle = Path(index[case]["bundle"])
    out = tmp / case
    # Premise of every overlap check below: conflictdiff compared the fixture's overlapprobe emits. It refuses
    # overlap emits that do not carry one shared measurement policy (3c9432e3), so a fixture emit without one
    # leaves nothing for the repair checks to read.
    diff_logs = sorted(bundle.glob("sweep/*/logs/diff-overlapprobe-*.txt"))
    refused = [f"{p.name}: {line.strip()}" for p in diff_logs for line in text(p).splitlines()
               if "REFUSED" in line or line.startswith("(exit ") and not line.startswith("(exit 0,")]
    check(len(diff_logs) == 3 and not refused,
          f"{case}: premise - conflictdiff compared each candidate's overlapprobe emits "
          f"({len(diff_logs)} diff logs; refusals {refused})")
    # The fixture engine's plan predates the `patches` field; the patches its candidate arms add are declared at
    # report time, the route evalreport documents for such plans (636ae180). Membership alone names no patch.
    pres, p, r, v = compile_case(case, bundle, out, declare_patches=("PatchA.esp", "PatchB.esp"))
    if pres is None:
        return

    def overlap_wording_control(regression: bool, policy_marker: bool = False):
        spec = importlib.util.spec_from_file_location("evalreport_overlap_wording", EVALREPORT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        bundle_view = module.Bundle(bundle)
        bundle_view.disposition = copy.deepcopy(bundle_view.disposition)
        bundle_view.plan = copy.deepcopy(bundle_view.plan)
        wanted = {"baseline", "candidate-02-patchb"}
        bundle_view.disposition["arms"] = {k: v for k, v in bundle_view.disposition["arms"].items() if k in wanted}
        bundle_view.plan["arms"] = [a for a in bundle_view.plan.get("arms") or [] if a.get("armId") in wanted]
        evaluation = bundle_view.disposition["evaluation"]
        for key in ("missing", "findings", "completedNullChecks", "completedCatalogues"):
            evaluation[key] = [row for row in evaluation.get(key) or [] if row.get("armId") in wanted]
        if regression:
            order = bundle_view.disposition["arms"]["candidate-02-patchb"]["orderEvaluation"]
            order["classification"] = "REGRESSION"
            order["regression"] = True
            order["perProbe"]["overlapprobe"] = {"verdict": "DIFFERENTIAL-VERIFIED", "repaired": 0,
                                                    "reclassified": 0, "created": 1, "newDefect": 1,
                                                    "attributed": 0, "unmeasured": 0}
            diff_path = bundle / "overlap-regression-wording-control.txt"
            diff_path.write_text("NEW     OVERLAP    FixtureTarget.esp  x  FixtureBase.esp   +1 record(s): created 1\n",
                                 encoding="utf-8")
            original_diff_path = bundle_view.diff_log_path
            bundle_view.diff_log_path = lambda check_id, arm_id: (
                diff_path.name if (check_id, arm_id) == ("geometry.object-overlap", "candidate-02-patchb")
                else original_diff_path(check_id, arm_id))
        priorities = module.load_json(module.PRIORITIES)
        if policy_marker:
            priorities["reader"]["comparisonTemplates"]["overlapReduction"] = (
                "POLICY OVERLAP REDUCTION {repairedCount} {candidateNoun}.")
            priorities["reader"]["verdictTemplates"]["overlapOnlyReduction"] = "POLICY OVERLAP VERDICT."
        concise = priorities.get("concise") or {}
        page = priorities.get("page") or {}
        settings = {"mode": "concise", "layout": concise.get("layout", "normal"), "maxPages": 8,
                    "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                             "baseFontPt": page.get("baseFontPt", 10.5)},
                    "rowsPerGroup": concise.get("rowsPerGroup", 3),
                    "figuresPerFinding": concise.get("figuresPerFinding", 1),
                    "reel": priorities.get("reel") or {}}
        original_bundle = module.Bundle
        try:
            module.Bundle = lambda _: bundle_view
            return module.compile_plan(bundle, {"bundle": {"complete": True},
                                                "evaluation": {"complete": False, "claim": "incomplete"}},
                                       settings, priorities, str(bundle))
        finally:
            module.Bundle = original_bundle

    reduction_control = overlap_wording_control(False)
    reduction_verdict = str(headline(reduction_control.get("reader")) or "").lower()
    check("reduces the number of pairs of overlapping objects" in reduction_verdict and "resolves some measured conflicts" not in reduction_verdict,
          f"{case}: compile_plan keeps an overlap-only reduction as candidates rather than a repair verdict")
    regression_control = overlap_wording_control(True)
    regression_verdict = str(headline(regression_control.get("reader")) or "").lower()
    check("introduces 1 new pair of overlapping objects" in regression_verdict and "new defect" not in regression_verdict,
          f"{case}: compile_plan keeps an overlap-only regression as a candidate rather than a defect verdict")
    policy_control = overlap_wording_control(False, policy_marker=True)
    policy_overlap = [f for f in policy_control.get("findings") or []
                      if f.get("checkId") == "geometry.object-overlap"
                      and f.get("kind") in ("repaired", "reclassified")]
    check(headline(policy_control.get("reader")) == "POLICY OVERLAP VERDICT."
          and any(str(f.get("readerPlain") or "").startswith("POLICY OVERLAP REDUCTION") for f in policy_overlap),
          f"{case}: compile_plan selects overlap reader wording from report-priorities policy")
    check_patch_direction(bundle)
    check_alternative_identity(bundle)
    check_missing_setup_naming(bundle)
    check_no_findings_headline(bundle)
    followup_title_control(bundle)
    followup_totals_wording_control(bundle)
    followup_identical_label_control(bundle)
    reader = pres.get("reader") or {}
    expect(reader.get("suppliedPatches"), ["PatchA.esp", "PatchB.esp"],
           f"{case}: patches added by candidate arms are named although no arm is a supplied combination")
    expect(reader.get("title"), "FixtureTarget.esp with PatchA.esp, PatchB.esp — patch review",
           f"{case}: the title names the patches rather than the comparison mods")
    check("The patches evaluated are PatchA.esp, PatchB.esp, added to the measured load order for comparison."
          in str(reader.get("scopeLine")), f"{case}: the opening says the comparisons add the patches (got {reader.get('scopeLine')!r})")
    stale = tmp / f"{case}-stale-presentation"
    shutil.copytree(out, stale)                     # beside its supporting evidence, so only the plan differs
    stale_plan = copy.deepcopy(pres)
    (stale_plan.get("reader") or {}).pop("sectionHeadings", None)
    (stale / "presentation.json").write_text(json.dumps(stale_plan), encoding="utf-8")
    stale_res = evalreport(["report", "--presentation", stale / "presentation.json", "--out", stale / "out", "--no-print"])
    check(stale_res.returncode == 2 and "presentation-needs-replan" in (stale_res.stdout or "") + (stale_res.stderr or ""),
          f"{case}: a presentation planned before section headings existed is refused by name, not rendered "
          f"(exit {stale_res.returncode})")
    unlabelled_plan = copy.deepcopy(pres)
    ((unlabelled_plan.get("scope") or {}).get("arms") or [{}])[-1].pop("readerLabel", None)
    (stale / "presentation.json").write_text(json.dumps(unlabelled_plan), encoding="utf-8")
    stale_res = evalreport(["report", "--presentation", stale / "presentation.json", "--out", stale / "out-unlabelled", "--no-print"])
    check(stale_res.returncode == 2 and "presentation-needs-replan" in (stale_res.stdout or "") + (stale_res.stderr or ""),
          f"{case}: a presentation planned before setups had reader labels is refused by name, not rendered "
          f"(exit {stale_res.returncode})")
    md, _ = assert_common(case, bundle, out, pres, common, v) or ("", "")
    fs = pres.get("findings") or []
    rg = o["regression"]
    f1 = fs[0] if fs else {}
    expect(f1.get("id"), rg["id"], f"{case}: first finding id", "D3 (1)")
    for k in ("kind", "priority", "checkId", "family", "armId", "verdict"):
        expect(f1.get(k), rg[k], f"{case}: F-001.{k}", "D3 (1) / D2")
    cnt = f1.get("counts") or {}
    for k, n in rg["counts"].items():
        expect(cnt.get(k), n, f"{case}: F-001.counts.{k} verbatim", "D3 (4) never netted")
    check(bool(f1.get("evidenceIds")), f"{case}: F-001 has evidence ids")
    chs = [x for x in pres.get("chapters") or [] if x.get("kind") == rg["inSomeChapterOfKind"] and "F-001" in (x.get("findingIds") or [])]
    expect(f1.get("readerRelevant"), rg["readerRelevant"], f"{case}: CLOBBERED-only regression reader relevance")
    expect(bool(chs), rg["inSomeChapter"], f"{case}: CLOBBERED-only regression chapter membership")
    reader_verdict = str((pres.get("reader") or {}).get("verdict") or "")
    opening = next((x for x in pres.get("chapters") or [] if x.get("kind") == "opening"), {})
    opening_narration = " ".join(str(c.get("narration") or "") for c in opening.get("captions") or [])
    check(bool(reader_verdict) and opening_narration.startswith(reader_verdict),
          f"{case}: opening chapter narration begins with the shared reader verdict")
    all_narration = " ".join(str(c.get("narration") or "") for x in pres.get("chapters") or []
                             for c in x.get("captions") or [])
    raw_terms = {"navownprobe", "overlapprobe", "DIFFERENTIAL-NEUTRAL", "DIFFERENTIAL-VERIFIED", "REGRESSION"}
    raw_terms.update(a.get("armId") for a in (pres.get("scope") or {}).get("arms") or [] if a.get("armId"))
    raw_terms.update((pres.get("scope") or {}).get("checks") or {})
    leaked = sorted(t for t in raw_terms if t and t in all_narration)
    expect(leaked, [], f"{case}: default chapter narration contains no probe, status, check-id or arm-id terms")
    overlap_reductions = [f for f in fs if f.get("kind") in ("repaired", "reclassified")
                          and f.get("checkId") == "geometry.object-overlap" and f.get("readerRelevant")]
    confirmed_repairs = [f for f in fs if f.get("kind") in ("repaired", "reclassified")
                         and f.get("checkId") != "geometry.object-overlap" and f.get("readerRelevant")
                         and int((f.get("counts") or {}).get("readerRepaired") or 0)]
    if overlap_reductions and not confirmed_repairs:
        reader_headline = str(headline(pres.get("reader")) or "")
        check("pairs of overlapping objects" in reader_headline.lower() and "resolves some measured conflicts" not in reader_headline.lower()
              and "resolves some measured conflicts" not in reader_verdict.lower(),
              f"{case}: overlap-candidate reductions do not become a repair claim in reader.verdict")
    check(rg["plainContainsNumber"] in [int(x) for x in re.findall(r"\b\d+\b", str(f1.get("plain") or ""))],
          f"{case}: F-001's sentence carries the bound number 1")
    low = str(f1.get("plain") or "").lower()
    for s in rg["plainDoesNotContain"]:
        check(s not in low, f"{case}: F-001's sentence does not say {s!r} (no netting)")
    if rg.get("plainNoAffirmativeNettingClaim"):
        expect(netting_claims(str(f1.get("plain") or "")), [],
               f"{case}: F-001's sentence makes no affirmative netting claim (a negated 'net' is allowed)", "D3 (4)")
    regs = [f for f in fs if f.get("kind") == "regression"]
    expect(len(regs), 1, f"{case}: exactly one regression finding", "D3 (1)")
    for spec in o["repairedFindings"]:
        hit = finding_where(pres, kind="repaired", armId=spec["armId"], checkId=spec["checkId"])
        check(bool(hit), f"{case}: a `repaired` finding for {spec['checkId']} [{spec['armId']}] (D3 (4))")
        if hit:
            expect((hit[0].get("counts") or {}).get("repaired"), spec["countsRepaired"], f"{case}: {hit[0].get('id')} counts.repaired", "D3 (4)")
            expect(hit[0].get("verdict"), spec["verdict"], f"{case}: {hit[0].get('id')} verdict verbatim", "D3 (3)")
            check(hit[0].get("priority", 0) > f1.get("priority", 0), f"{case}: {hit[0].get('id')} priority > the regression's")
            if spec["checkId"] == "geometry.object-overlap":
                reader_plain = str(hit[0].get("readerPlain") or "").lower()
                check("overlapping objects" in reader_plain and "worth a look in game" in reader_plain,
                      f"{case}: overlap reductions are still said to be worth a look in game (R10 wording)")
                check("repaired 1 existing problem" not in reader_plain,
                      f"{case}: overlap reductions are not narrated as confirmed problem repairs")
                comparison_narration = " ".join(str(cap.get("narration") or "") for chapter in pres.get("chapters") or []
                                                if chapter.get("kind") == "comparison" and hit[0].get("id") in (chapter.get("findingIds") or [])
                                                for cap in chapter.get("captions") or []).lower()
                check("overlapping objects" in comparison_narration and "repaired 1 existing problem" not in comparison_narration,
                      f"{case}: comparison narration preserves the overlap-candidate qualification")
    for s in o["report"]["mdContains"]:
        check(s in md, f"{case}: report.md contains {s!r}")
    rep_ids = [f.get("id") for f in fs if f.get("kind") == "repaired"]
    check("F-001" in text(out / "evidence-index.md"), f"{case}: raw context-only regression remains in the evidence index")
    low = md.lower()
    for s in o["report"]["mdDoesNotContain"]:
        check(s.lower() not in low, f"{case}: report.md does not contain {s!r}")
    if o["report"].get("mdNoAffirmativeNettingClaim"):
        expect(netting_claims(md), [], f"{case}: report.md makes no affirmative netting claim (a negated 'net' is allowed)", "D3 (4)")
    pm = load(out / "presentation-manifest.json") or {}
    expect((pm.get("print") or {}).get("status"), o["report"]["print"]["status"], f"{case}: print.status", "D4")
    check(isinstance((pm.get("print") or {}).get("pages"), int) and pm["print"]["pages"] <= o["report"]["print"]["pagesAtMost"],
          f"{case}: print.pages <= 4 (got {(pm.get('print') or {}).get('pages')!r})")
    # A-11: when the limit is unreachable, the kept rendition is the least abridged content-preserving step
    pr_ = pm.get("print") or {}
    ol = o["report"].get("overLimitKept")
    if ol and pr_.get("status") == "over-limit":
        kept = pr_.get("kept") or {}
        expect(kept.get("layout"), ol["layout"], f"{case}: print.kept.layout on over-limit", "A-11")
        expect(kept.get("rowsPerGroup"), (pres.get("settings") or {}).get("rowsPerGroup"),
               f"{case}: print.kept.rowsPerGroup equals settings.rowsPerGroup", "A-11")
        expect(kept.get("figuresPerFinding"), ol["figuresPerFinding"], f"{case}: print.kept.figuresPerFinding", "A-11")
        check(ol["reasonMentions"] in str(pr_.get("reason") or "").lower(),
              f"{case}: print.reason mentions {ol['reasonMentions']!r} (got {str(pr_.get('reason'))[:120]!r}) (A-11)")


def case_r5(tmp, index, common):
    case = "R5-media-mismatch-same-basenames"
    o = load(EXPECTED / f"{case}.json")
    if not cli_present(case):
        return
    bundle = Path(index[case]["bundle"])
    out = tmp / case
    pres, p, r, v = compile_case(case, bundle, out)
    if pres is None:
        return
    md, idx = assert_common(case, bundle, out, pres, common, v) or ("", "")
    media = pres.get("media") or []
    assessed = [m for m in media if m.get("assessed") is True]
    ar = o["assessedRules"]
    expect(len(assessed), ar["count"], f"{case}: assessed media rows", "D5")
    ids = [m.get("id") for m in assessed]
    check(len(set(ids)) == len(ids), f"{case}: assessed media ids distinct")
    check(len({m.get("sha256") for m in assessed}) == len(assessed), f"{case}: assessed media sha256 distinct")
    check(len({m.get("path") for m in assessed}) == len(assessed), f"{case}: assessed media paths distinct")
    for m in assessed:
        # a media path is written relative to the app bundle the overlay is read with: under the overlay's media/
        check(("/" + ar["pathStartsWith"]) in ("/" + str(m.get("path", "")).replace("\\", "/")), f"{case}: {m.get('id')} path under media/")
        check(str(m.get("path", "")).endswith(ar["pathBasenameEndsWith"]), f"{case}: {m.get('id')} keeps the basename cell-view.png")
        check((bundle / str(m.get("path"))).exists(), f"{case}: {m.get('id')} exists under the bundle")
        check(any(m.get("id") in (x.get("mediaIds") or []) for x in pres.get("chapters") or []), f"{case}: {m.get('id')} is in a chapter")
        check(any(cap.get("mediaId") == m.get("id") for x in pres.get("chapters") or [] for cap in x.get("captions") or []),
              f"{case}: {m.get('id')} has a caption")
        hosts = [f for f in pres.get("findings") or [] if m.get("id") in (f.get("mediaIds") or [])
                 and f.get("kind") == "finding"]
        check(any((f.get("counts") or {}).get("groupKind") == ar["hostFindingGroupKind"]
                  and f.get("readerRelevant") is True for f in hosts),
              f"{case}: {m.get('id')} illustrates active {ar['hostFindingGroupKind']} reader evidence")
    for spec in o["assessed"]:
        hit = [m for m in assessed if (m.get("subject") or {}).get("armId") == spec["subjectArmId"]]
        check(len(hit) == 1, f"{case}: one assessed row on arm {spec['subjectArmId']}")
        if hit:
            for k in ("mediaKind", "fingerprintSource", "state"):
                expect(hit[0].get(k), spec[k], f"{case}: media on {spec['subjectArmId']} {k}", "D5")
    un = [m for m in media if m.get("assessed") is not True]
    expect(len(un), o["unassessedRules"]["count"], f"{case}: unassessed media rows", "D5")
    listed = json.dumps((pres.get("unassessed") or {}).get("media") or [])
    ch_media = {mid for x in pres.get("chapters") or [] for mid in (x.get("mediaIds") or [])}
    unass_f = " ".join(json.dumps(f) for f in pres.get("findings") or [] if f.get("kind") == "unassessed")
    for spec in o["unassessed"]:
        hit = [m for m in un if Path(str(m.get("path") or "")).name == spec["basename"]]
        check(len(hit) == 1, f"{case}: one unassessed row for {spec['basename']}")
        if hit:
            expect(hit[0].get("reason"), spec["reason"], f"{case}: {spec['basename']} reason", "D5")
            check(hit[0].get("id") not in ch_media if hit[0].get("id") else True, f"{case}: {spec['basename']} is in no chapter")
        check(spec["basename"] in listed, f"{case}: {spec['basename']} is listed under unassessed.media")
        check(spec["basename"] in unass_f, f"{case}: a finding of kind unassessed names {spec['basename']} (D3 (7))")
    # A-14: a picture illustrates an arm's OWN configuration, never the reverse comparison's
    ap = o.get("armPartRules") or {}
    if ap:
        rev = [f.get("id") for f in pres.get("findings") or [] if f.get("part") == "reverse" and f.get("mediaIds")]
        expect(rev, [], f"{case}: no reverse-part finding carries mediaIds", "A-14")
        cand = [m for m in assessed if (m.get("subject") or {}).get("armId") == ap["candidateArm"]]
        check(len(cand) == 1, f"{case}: one accepted item on {ap['candidateArm']} (A-14)")
        if cand:
            mid = cand[0].get("id")
            hosts = [f.get("id") for f in pres.get("findings") or []
                     if f.get("armId") == ap["candidateArm"] and f.get("part") in ap["hostParts"] and mid in (f.get("mediaIds") or [])]
            check(bool(hosts), f"{case}: the candidate-arm item {mid} sits on that arm's {'/'.join(ap['hostParts'])} finding (A-14), got {hosts}")
    for s in o["report"]["mdContains"]:
        check(s in md, f"{case}: report.md contains {s!r}")
    imgs = [p for p in image_paths(md) if (out / p).exists()]
    check(len(set(imgs)) >= 2, f"{case}: report.md embeds two distinct existing image paths (got {sorted(set(imgs))})")
    for m in assessed:
        check(str(m.get("sha256")) in idx, f"{case}: evidence-index.md lists {m.get('id')}'s sha256")
    pm = load(out / "presentation-manifest.json") or {}
    expect((pm.get("print") or {}).get("status"), o["report"]["print"]["status"], f"{case}: print.status", "D4")


def case_r6(tmp, index, common):
    case = "R6-changed-or-missing-source"
    o = load(EXPECTED / f"{case}.json")
    if not cli_present(case):
        return
    for spec in o["refusals"]:
        b = Path(index[case][spec["damage"]]["bundle"])
        out = tmp / f"{case}-{spec['damage']}"
        res = do_plan(b, out)
        check(res.returncode != 0, f"{case}/{spec['damage']}: plan exits non-zero (got {res.returncode})")
        blob = scrub(res, b) + scrub(res, out)
        check(spec["refusedByName"] in blob, f"{case}/{spec['damage']}: refused by name {spec['refusedByName']!r} (D1)")
        pres = load(out / "presentation.json")
        ok_pres = pres is not None and ((pres.get("scope") or {}).get("integrity") or {}).get("ok") is True
        check(not ok_pres, f"{case}/{spec['damage']}: no presentation with integrity ok was produced")
        if spec.get("verifyBundleSays"):
            vj = load(out / "verify.json") or {}                 # plan writes the verdict to OUT, never the bundle
            check(bool((vj.get("bundle") or {}).get("mismatched")), f"{case}/{spec['damage']}: verify-bundle listed the altered file under mismatched")


def resolve_placeholders(s: str, pres: dict) -> str:
    arms = {}
    for a in (pres.get("scope") or {}).get("arms") or []:
        arms[Path(str(a.get("candidateFile") or "")).stem] = a.get("armId")
        arms[a.get("armId")] = a.get("armId")
    return re.sub(r"\{\{arm:([^}]+)\}\}", lambda m: str(arms.get(m.group(1), m.group(1))), s)


def case_fidelity(tmp, index, common):
    case = "fidelity"
    if not cli_present(case):
        return
    bundle = Path(index["R4-repaired-plus-regression"]["bundle"])
    out = tmp / "fidelity-base"
    res = do_plan(bundle, out)
    pres = load(out / "presentation.json")
    check(res.returncode == 0 and pres is not None, f"{case}: R4's presentation compiled")
    if not pres:
        return
    template = {f.get("id"): f.get("plain") for f in pres.get("findings") or []}
    for pair_file in sorted(FIDELITY.glob("*.json")):
        pair = load(pair_file)
        name = f"{case}/{pair['case']}"
        answers = {k: resolve_placeholders(v, pres) for k, v in pair["answers"].items()}
        work = tmp / f"fidelity-{pair_file.stem}"
        work.mkdir(parents=True, exist_ok=True)
        pcopy = work / "presentation.json"
        shutil.copy2(out / "presentation.json", pcopy)
        afile = work / "answers.json"
        afile.write_text(json.dumps({"answers": answers}, indent=1), encoding="utf-8")
        # every answer judged faithful, so these pairs test the lexical rules alone (the judge's gate is C2022-C2027)
        jfile = work / "judgements.json"
        jfile.write_text(json.dumps({"presentationId": pres.get("presentationId"), "judgements": [
            {"id": k, "textSha256": hashlib.sha256(v.encode("utf-8")).hexdigest(), "faithful": True, "facts": [],
             "added": [], "reason": "fixture"} for k, v in answers.items()]}, indent=1), encoding="utf-8")
        r = evalreport(["narrate-apply", "--presentation", pcopy, "--answers", afile, "--judgements", jfile])
        check(r.returncode == 0, f"{name}: narrate-apply exits 0 (got {r.returncode})")
        if r.returncode != 0:
            print(r.stdout[-1500:]); print(r.stderr[-1500:])
        after = load(pcopy) or {}
        nar = after.get("narration") or {}
        refused = nar.get("refused") or []
        rids = sorted(x.get("id") for x in refused if isinstance(x, dict))
        want_ref = sorted(x["id"] for x in pair["expected"]["refused"])
        expect(rids, want_ref, f"{name}: refused ids", "D7")
        for spec in pair["expected"]["refused"]:
            rule = " ".join(str(x.get("rule", "")) for x in refused if isinstance(x, dict) and x.get("id") == spec["id"]).lower()
            check(any(k in rule for k in spec["ruleKeywords"]), f"{name}: the refusal rule names {spec['ruleKeywords']} (got {rule!r})")
        plains = {f.get("id"): f.get("plain") for f in after.get("findings") or []}
        reader_plains = {f.get("id"): f.get("readerPlain") for f in after.get("findings") or []}
        for fid, want in pair["afterApply"].items():
            if want.get("plainEqualsAnswer"):
                expect(plains.get(fid), answers[fid], f"{name}: {fid} plain is the accepted answer", "D7")
                expect(reader_plains.get(fid), answers[fid], f"{name}: {fid} reader prose is the accepted answer", "D7")
                check(fid in (pair["expected"]["accepted"]), f"{name}: {fid} was expected accepted")
            if want.get("plainEqualsTemplate"):
                expect(plains.get(fid), template.get(fid), f"{name}: {fid} keeps the template sentence", "D7")
        others = [fid for fid in plains if fid not in answers and plains[fid] != template.get(fid)]
        expect(others, [], f"{name}: no other finding's sentence changed", "D7")


# ------------------------------------------------- the reuse source disclosure, all three states

def case_r8(tmp, index, common) -> None:
    """R8: a reused import's SOURCE disclosure survives to the reader in all three of its states.

    A declared reuse (bundle contract 4.3, source rule) leaves one of three things to tell a reader, and
    they are different claims:

      other-file  the capture names a different candidate file, which is the case the exception exists for;
      unstated    the capture names NO candidate file at all - a real limitation on what can be checked;
      (neither)   the two candidate files have the same name, so there is nothing extra to say, and saying
                  "measured against <this setup's own file>" beside "not measured for this setup" would
                  read as a contradiction.

    Every consumer used to filter on a truthy filename, so `unstated` was indistinguishable from the
    no-mismatch case at every layer after the receipt (Piranha's handback, 2026-09-17). This exercises the
    ACTUAL compiler function and the ACTUAL index templates, not a reimplementation of either."""
    label = "R8/reuse-source-disclosure"
    spec = importlib.util.spec_from_file_location("evalreport_r8", EVALREPORT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    def limit(captured, arm_candidate="candidate:this-arm.json"):
        return {"capturedAgainst": captured, "armCandidate": arm_candidate,
                "declaredSource": {"planId": "SRCPLAN", "armId": "src-arm"},
                "detail": "declared reuse"}

    # one receipt per state, in the shape the controller writes
    arms = {
        "arm-other": [limit("candidate:another-file.json")],
        "arm-unstated": [limit(None)],
        "arm-same": [],                      # rule (f) satisfied: no source-rule limit exists
    }
    receipts, disposition_rows = {}, []
    for i, (arm, limits) in enumerate(sorted(arms.items()), start=1):
        rid = f"receipt{i:02d}"
        receipts[rid] = {
            "receiptId": rid, "checkId": "mcp.check", "armId": arm, "part": None, "planId": "PLAN",
            "validation": {"accepted": True, "rejections": [], "identityLimits": limits},
            "consumed": {"configuration": {"source": "candidate:whatever-the-capture-said.json"}},
            "imported": {"origin": "reused", "reusedFrom": {"planId": "SRCPLAN", "armId": "src-arm",
                                                            "resultSha256": "a" * 64},
                         "reusedFromVerified": False, "reusedWithin": "another-evaluation",
                         "reusedFromNote": "declared", "capturedUtc": "2026-09-17T00:00:00Z",
                         "tool": {"name": "t"}, "sourceSha256": "b" * 64},
        }
        disposition_rows.append({"checkId": "mcp.check", "armId": arm, "receiptId": rid, "origin": "reused"})
    # the fields the real pipeline attaches after imported_evidence() and before rendering

    view = types.SimpleNamespace(
        receipts=receipts,
        disposition={"importedEvidence": disposition_rows, "evaluation": {}},
    )
    got = module.imported_evidence(view)
    by_arm = {r["armId"]: r for r in got["rows"]}
    expect(sorted(by_arm), ["arm-other", "arm-same", "arm-unstated"],
           f"{label}: premise - the compiler returned a row per arm")

    # ---- the compiler tells the three states apart
    expect(by_arm["arm-other"].get("sourceDisclosure"), "other-file",
           f"{label}: a capture naming another file is `other-file`", "4.3 source rule")
    expect(by_arm["arm-other"].get("capturedAgainst"), "candidate:another-file.json",
           f"{label}: and the name reaches the row")
    expect(by_arm["arm-unstated"].get("sourceDisclosure"), "unstated",
           f"{label}: a capture naming NO file is `unstated`, not silence", "4.3 source rule")
    expect(by_arm["arm-unstated"].get("capturedAgainst"), None,
           f"{label}: with no filename, because the capture named none")
    expect(by_arm["arm-unstated"].get("armCandidate"), "candidate:this-arm.json",
           f"{label}: and this arm's own file, so the reader knows what was NOT confirmed")
    check("sourceDisclosure" not in by_arm["arm-same"],
          f"{label}: matching names carry NO disclosure - there is nothing extra to say "
          f"(got {by_arm['arm-same'].get('sourceDisclosure')!r})")
    check(not by_arm["arm-same"].get("capturedAgainst"),
          f"{label}: and no filename either")

    # ---- and the two index renditions say three different things
    got["limit"] = "These labels catch evidence filed under the wrong evaluation by mistake."
    for r in got["rows"]:
        r.setdefault("findingIds", [])
        r.setdefault("receiptEvidenceId", None)
        r.setdefault("indexOrigin", "captured for another evaluation and reused here")
    shown = {"imports": got, "findings": [], "detailAside": [], "groups": [], "media": [], "evidence": [], "omissions": [],
             "chapters": [], "presentationId": "P", "generatedUtc": "now", "layout": "normal",
             "reader": {"prediction": {"record": [], "predictionArm": "baseline"}},
             "source": {"planId": "PLAN", "bundleDir": ".", "manifestSha256": "", "dispositionSha256": "",
                        "ledgerSha256": ""},
             "scope": {"targets": [], "registryVersion": "", "planCreatedUtc": "", "runStartedUtc": "",
                       "bundleCreatedUtc": "", "dispositionCreatedUtc": "", "states": {}, "arms": [],
                       "explicitDecisions": {}, "completion": {}, "integrity": {}, "checks": {}}}
    for fmt, autoescape in (("md", False), ("html", True)):
        body = module.env(autoescape).get_template(f"evidence-index.{fmt}.j2").render(p=shown, css="")
        check("states it was measured against" in body and "another-file.json" in body,
              f"{label}/{fmt}: the other-file state names the file the capture gave")
        check("names NO candidate file" in body,
              f"{label}/{fmt}: the unstated state is SAID, not dropped for want of a filename")
        check("this-arm.json" in body,
              f"{label}/{fmt}: and the unstated state names this arm's own file for contrast")
        # the no-mismatch row must not acquire either sentence
        expect(body.count("names NO candidate file"), 1,
               f"{label}/{fmt}: exactly one row is unstated", "4.3 source rule")
        expect(body.count("states it was measured against"), 1,
               f"{label}/{fmt}: exactly one row names another file - and the unstated sentence, which also "
               f"contains the words 'measured against', is not miscounted as one", "4.3 source rule")


CASES = {
    "R1-intact-incomplete": case_r1,
    "R2-null-vs-missing": case_r2,
    "R3-many-findings-long-names": case_r3,
    "R4-repaired-plus-regression": case_r4,
    "R5-media-mismatch-same-basenames": case_r5,
    "R6-changed-or-missing-source": case_r6,
    "R7-import-origin": case_r7,
    "R8-reuse-source-disclosure": case_r8,
    "fidelity": case_fidelity,
}


def main() -> int:
    want = sys.argv[1] if len(sys.argv) > 1 else ""
    tmp = Path(tempfile.mkdtemp(prefix="hc-evalreport-test-"))
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(tmp / "runlog"))
    os.environ["PYTHONIOENCODING"] = "utf-8"
    common = load(EXPECTED / "common.json")
    try:
        import make_report_fixtures as MF
        import ai_report_fixtures as AIF
        check_direction_inference()
        check_stylesheet_escaping()
        print("=== fixtures ===")
        try:
            MF.T.use_conflictdiff(tmp)
        except SystemExit as ex:
            print(ex)
            FAILURES.append("conflictdiff did not build")
            return 1
        print(MF.T.DIFF_SOURCE)
        print(MF.T.diff_exe_identity())
        t0 = time.time()
        try:
            only = None
            if want and want.lower().startswith("fidelity"):
                only = "R4"
            elif want:
                only = want
            index = MF.generate(tmp / "fixtures", only, ai_hook=AIF.add_overlays)
            for entry in [e for e in index.values() if isinstance(e, dict)] + [
                    x for e in index.values() if isinstance(e, dict) for x in e.values() if isinstance(x, dict)]:
                if entry.get("aiBundle"):
                    AI_OF[str(Path(entry["bundle"]))] = entry["aiBundle"]
            check(True, f"fixtures generated under {tmp / 'fixtures'} ({time.time() - t0:.1f}s)")
        except SystemExit as ex:
            check(False, f"fixture generation failed: {ex}")
            return 1
        for name, fn in CASES.items():
            if want and not name.startswith(want) and not want.startswith(name):
                continue
            print(f"\n=== {name} ===")
            t0 = time.time()
            try:
                fn(tmp, index, common)
            except Exception as ex:                                  # a crash is a failure, never a skip
                check(False, f"{name}: raised {type(ex).__name__}: {ex}")
            print(f"  ({time.time() - t0:.1f}s)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print()
    if DISAGREEMENTS:
        print("ORACLE vs CODE disagreements (the oracle is NOT re-based on the code; these go to Fable):")
        for d in DISAGREEMENTS:
            print("  - " + d)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
