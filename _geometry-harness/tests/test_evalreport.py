"""The report compiler against the oracle (Q45).

    python _geometry-harness/tests/test_evalreport.py            # from the checkout root
    python _geometry-harness/tests/test_evalreport.py R4         # one case (a prefix match); `fidelity` too

Dependency-free; exits 1 on failure. The fixtures are REAL schema-2 bundles made at test time by driving
`evalrun.py` over `test_evalrun.py`'s engine (`fixtures/evalreport/make_report_fixtures.py`); the compiler
under test is `_geometry-harness/evalreport.py` (`plan` / `report` / `verify` / `narrate-apply`). What is
asserted comes from `fixtures/evalreport/expected/*.json`, written from PLAN.md's D1-D7 and the packet's
acceptance table BEFORE the compiler existed. Where the code and the oracle disagree the oracle is not edited
to match: the test fails and the disagreement is reported with both values and the rule.

A missing compiler, a missing conflictdiff.exe or a missing browser is a FAILURE that names the file - never
a skip.
"""
from __future__ import annotations

import copy
import hashlib
import importlib.util
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
HARNESS = HERE.parent
FIXT = HERE / "fixtures" / "evalreport"
EXPECTED = FIXT / "expected"
FIDELITY = FIXT / "fidelity"
# HOUSECARL_EVALREPORT names another tree's compiler to test (an integration head); the default is this
# checkout's own. HOUSECARL_EVALRUN does the same for the controller the fixtures are built with.
EVALREPORT = Path(os.environ.get("HOUSECARL_EVALREPORT") or (HARNESS / "evalreport.py"))
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

def do_plan(bundle: Path, out: Path, exhaustive=False):
    args = ["plan", "--bundle", bundle, "--out", out] + (["--exhaustive"] if exhaustive else [])
    return evalreport(args)


def compile_case(case: str, bundle: Path, out: Path, exhaustive=False):
    """plan + report + verify; returns (presentation, plan result, report result, verify result)."""
    p = do_plan(bundle, out, exhaustive)
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
    # evidence
    man_ev = {e.get("id"): e for e in man.get("evidence") or []}
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
    p2 = do_plan(bundle, out2, exhaustive=(st.get("mode") == "exhaustive"))
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
    check("size: A4" in html.replace("size:A4", "size: A4"), f"{case}: report.html has @page size A4 (D4)")
    small = [m for m in re.findall(r"font-size\s*:\s*([0-9.]+)pt", html) if float(m) < 9]
    expect(small, [], f"{case}: report.html has no font below 9pt", "D4")
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
              "both": {"baseline": base + [plugin("Other.esp")], "candidate-02-patchb": base + [plugin("Patch.esp")]}}[variant]
    for arm_id, plugins in orders.items():
        arms[arm_id]["requested"]["plugins"] = plugins
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
    expect(reader.get("verdict"), "Removing Patch.esp introduces a new defect in a check that ran.",
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
    expect(reader.get("verdict"), "Adding Patch.esp introduces a new defect in a check that ran.",
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
    check("No separate patch plugin was included in the comparison." in str(reader.get("scopeLine")),
          f"{label}/none: the opening keeps the no-separate-patch wording (got {reader.get('scopeLine')!r})")
    mods = reader.get("comparisonMods") or []
    expect(reader.get("title"), "FixtureTarget.esp" + (f" with {', '.join(mods[:2])}" if mods else "") + " — patch review",
           f"{label}/none: with no patch the title keeps the comparison mods")
    expect(reader.get("verdict"), "Removing FixtureTarget.esp introduces a new defect in a check that ran.",
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
    expect(reader.get("verdict"), "A tested alternative setup introduces a new defect in a check that ran.",
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
        verdict = str((plan.get("reader") or {}).get("verdict") or "")
        check(verdict.startswith(f"{subject} introduces 1 new overlap candidate for visual review."),
              f"{label}/{variant}/overlap-regression: the overlap-only Result names the change (got {verdict!r})")
        got = plain_of(plan, "candidate-02-patchb", ("regression",), "geometry.object-overlap")
        check(got.startswith(f"{subject} introduced 1 new overlap candidate for visual review."),
              f"{label}/{variant}/overlap-regression: the overlap finding names the change (got {got!r})")
        check("supplied setup" not in md.lower() and "supplied setup" not in html.lower(),
              f"{label}/{variant}/overlap-regression: no supplied-setup wording in report.md or report.html")
        plan, md, html = patch_direction_control(bundle, variant, "overlap-reduction")
        verdict = str((plan.get("reader") or {}).get("verdict") or "")
        check(verdict.startswith(f"{subject} reduces the number of overlap candidates reported by the checks that ran."),
              f"{label}/{variant}/overlap-reduction: the overlap-only Result names the change (got {verdict!r})")
        got = plain_of(plan, "candidate-02-patchb", ("repaired", "reclassified"), "geometry.object-overlap")
        check(f"no longer present after {subject[:1].lower() + subject[1:]}." in got,
              f"{label}/{variant}/overlap-reduction: the overlap reduction names the change (got {got!r})")
        check("supplied setup" not in md.lower() and "supplied setup" not in html.lower(),
              f"{label}/{variant}/overlap-reduction: no supplied-setup wording in report.md or report.html")

    # (g) the repaired-only and regression-plus-repair Results, adding / removing / several arms
    for variant in ("adds", "removes", "mixed"):
        subject = subjects[variant]
        plan, md, html = patch_direction_control(bundle, variant, "contact-repair")
        expect((plan.get("reader") or {}).get("verdict"),
               f"{subject} resolves some measured conflicts and no new defect was reported by the checks that ran.",
               f"{label}/{variant}/contact-repair: the repaired-only Result names the change")
        plan, md, html = patch_direction_control(bundle, variant, "contact-regression-repair")
        expect((plan.get("reader") or {}).get("verdict"),
               f"{subject} resolves some measured conflicts, but it also introduces a new defect in a check that ran.",
               f"{label}/{variant}/contact-regression-repair: the regression-plus-repair Result names the change")

    # (h) one arm that adds one plugin and removes another names both changes
    plan, md, html = patch_direction_control(bundle, "both")
    reader = plan.get("reader") or {}
    expect(reader.get("verdict"), "Adding Patch.esp and removing Other.esp introduces a new defect in a check that ran.",
           f"{label}/both: the Result names the addition and the removal")
    check("The patches evaluated are Other.esp, Patch.esp, added or removed for comparison." in str(reader.get("scopeLine")),
          f"{label}/both: the opening says the comparison adds or removes them (got {reader.get('scopeLine')!r})")
    check("## Problems introduced by adding Patch.esp and removing Other.esp" in md and "supplied setup" not in md.lower(),
          f"{label}/both: the problems heading names both changes")

    # (i) the setups-compared row joins its clauses without a stray separator, in both directions
    check(": adds Patch.esp; removes Other.esp." in md and ": adds Patch.esp; removes Other.esp." in html,
          f"{label}/both: an arm that adds and removes lists both, separated by a semicolon")
    plan, md, html = patch_direction_control(bundle, "removes")
    check("- **Tested alternative setup**: removes Patch.esp." in md
          and "<strong>Tested alternative setup</strong>: removes Patch.esp." in html,
          f"{label}/removes: a removal-only row reads 'Tested alternative setup: removes …' with no leading semicolon")


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
    pres, p, r, v = compile_case(case, bundle, out)
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
    reduction_verdict = str((reduction_control.get("reader") or {}).get("verdict") or "").lower()
    check("reduces the number of overlap candidates" in reduction_verdict and "resolves some measured conflicts" not in reduction_verdict,
          f"{case}: compile_plan keeps an overlap-only reduction as candidates rather than a repair verdict")
    regression_control = overlap_wording_control(True)
    regression_verdict = str((regression_control.get("reader") or {}).get("verdict") or "").lower()
    check("introduces 1 new overlap candidate" in regression_verdict and "new defect" not in regression_verdict,
          f"{case}: compile_plan keeps an overlap-only regression as a candidate rather than a defect verdict")
    policy_control = overlap_wording_control(False, policy_marker=True)
    policy_overlap = [f for f in policy_control.get("findings") or []
                      if f.get("checkId") == "geometry.object-overlap"
                      and f.get("kind") in ("repaired", "reclassified")]
    check((policy_control.get("reader") or {}).get("verdict") == "POLICY OVERLAP VERDICT."
          and any(str(f.get("readerPlain") or "").startswith("POLICY OVERLAP REDUCTION") for f in policy_overlap),
          f"{case}: compile_plan selects overlap reader wording from report-priorities policy")
    check_patch_direction(bundle)
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
        check("overlap candidates" in reader_verdict.lower() and "resolves some measured conflicts" not in reader_verdict.lower(),
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
                check("overlap candidate" in reader_plain and "visual review" in reader_plain,
                      f"{case}: overlap reductions remain candidates for visual review")
                check("repaired 1 existing problem" not in reader_plain,
                      f"{case}: overlap reductions are not narrated as confirmed problem repairs")
                comparison_narration = " ".join(str(cap.get("narration") or "") for chapter in pres.get("chapters") or []
                                                if chapter.get("kind") == "comparison" and hit[0].get("id") in (chapter.get("findingIds") or [])
                                                for cap in chapter.get("captions") or []).lower()
                check("overlap candidate" in comparison_narration and "repaired 1 existing problem" not in comparison_narration,
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
        check(str(m.get("path", "")).startswith(ar["pathStartsWith"]), f"{case}: {m.get('id')} path under media/")
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
            vj = load(b / "verify.json") or {}
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
        r = evalreport(["narrate-apply", "--presentation", pcopy, "--answers", afile])
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


CASES = {
    "R1-intact-incomplete": case_r1,
    "R2-null-vs-missing": case_r2,
    "R3-many-findings-long-names": case_r3,
    "R4-repaired-plus-regression": case_r4,
    "R5-media-mismatch-same-basenames": case_r5,
    "R6-changed-or-missing-source": case_r6,
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
        check_direction_inference()
        print("=== fixtures ===")
        t0 = time.time()
        try:
            only = None
            if want and want.lower().startswith("fidelity"):
                only = "R4"
            elif want:
                only = want
            index = MF.generate(tmp / "fixtures", only)
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
