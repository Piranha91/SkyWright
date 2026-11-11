#!/usr/bin/env python
"""Known answers for the second report and reel batch, C1800-C1819: the record-flag table shared by the report lane
and orderleads, the reel's mod names, the patch search's listings, the author-advice count and the reel's media from
further siblings. Each case drives the real compiler, record comparator, reel planner, evaluator spec or plan command
on a small fixture and asserts what a reader sees or what a count says.

    python _geometry-harness/tests/test_report_reel_batch_2.py            every case
    python _geometry-harness/tests/test_report_reel_batch_2.py C1803 ...   the named cases (a prefix match)
Exit 0 all pass, 1 a case failed.
"""
from __future__ import annotations

import contextlib
import copy
import io
import json
import os
import re
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
import test_evalreport_prediction as P  # noqa: E402  (sets the no-settings environment before evalreport loads)
import evalreport as R  # noqa: E402
import reel_plan  # noqa: E402

ROOT = HERE.parent.parent
SCOPE_FILE = ROOT / ".claude/skills/world-edit-compatibility/references/record-field-scope.json"
REGISTRY = ROOT / ".claude/skills/world-edit-compatibility/references/check-registry.json"
BASE, A, B, OTHER, PATCH = P.BASE, P.MOD_A, P.MOD_B, P.OTHER, P.PATCH
CONTACT, RECORDS = P.CONTACT, P.RECORDS
ADVICE = "patch.author-advice"
P.PROBE.setdefault(ADVICE, "adviceprobe")


def pred_of(plan):
    return (plan.get("reader") or {}).get("prediction") or {}


# ------------------------------------------------------------------- 1. the record-flag table (report lane)

FLAG_C, FLAG_P, NAME_N = f"00F801:{BASE}", f"00F802:{BASE}", f"00F803:{BASE}"


def flag_bundle():
    """An older bundle's record rows, as the record stage wrote them before 3028ca48: a cell whose flags differ only
    in the compressed-storage bit, in both of Mutagen's views (Cut Content's 120 cells); a reference whose Persistent
    flag is lost, seen in both views (Hroggar's three PlacedNpc rows); and one ordinary lost field."""
    rows = [P.lost_row(OTHER, A, FLAG_C, "SkyrimMajorRecordFlags", "0", "Compressed", "Compressed", before=BASE),
            P.lost_row(OTHER, A, FLAG_C, "MajorFlags", "0", "262144", "262144", before=BASE),
            P.lost_row(OTHER, A, FLAG_P, "MajorFlags", "Persistent", "0", "0", before=BASE),
            P.lost_row(OTHER, A, FLAG_P, "SkyrimMajorRecordFlags", "1024", "0", "0", before=BASE),
            P.lost_row(OTHER, A, NAME_N, "Name", "o", "a", "v", before=BASE)]
    arms = [P.arm("baseline", [BASE, OTHER, A], kind="baseline"), P.arm("without-a", [BASE, OTHER])]
    enr = {(RECORDS, "baseline"): rows, (RECORDS, "without-a"): []}
    return P.compile_and_render(arms, {(CONTACT, a_): [] for a_ in ("baseline", "without-a")}, [A],
                                count_only={k: max(1, len(v)) for k, v in enr.items()}, enrichment=enr,
                                masters={OTHER: [BASE], A: [BASE]})


def c1800_compressed_only_flags_are_no_lost_change():
    """Item 1 (Cut Content's bundle: 240 Cell flag rows that differ only in Compressed): a record-flags row whose two
    values differ only in the compressed-storage bit is kept in the record and said apart, never a lost change."""
    plan, md, _ = flag_bundle()
    got = [(e.get("field"), e.get("admission")) for e in pred_of(plan).get("record") or [] if e.get("formKey") == FLAG_C]
    ok = bool(got) and all(a_ != "admitted" for _, a_ in got)
    return ok, f"the compressed-only cell's rows {got}"


def c1801_one_header_change_counted_once():
    """Item 1 (Hroggar's bundle: 15 PlacedNpc rows, each one Persistent change seen as MajorFlags and as
    SkyrimMajorRecordFlags): one header change is one lost change, so the item counts it once."""
    plan, md, _ = flag_bundle()
    pred = pred_of(plan)
    got = [(e.get("field"), e.get("admission")) for e in pred.get("record") or [] if e.get("formKey") == FLAG_P]
    item = next((i for i in pred.get("items") or [] if i.get("checkId") == RECORDS), {})
    ok = sum(1 for _, a_ in got if a_ == "admitted") == 1 and item.get("count") == 2
    return ok, f"the Persistent reference's rows {got}; the record item counts {item.get('count')} (want 2)"


def c1802_record_rows_mask_and_dedupe_the_flags():
    """Item 1: the record-rows enrichment (recordrows.derive on an import's own transcripts, which a re-enrichment of
    an older bundle runs) judges record flags as the record stage does: a compressed-only difference is no row, and
    one header change is one row."""
    import test_recordrows as TR
    import recordrows as RR
    fk1, fk2 = "000811:Skyrim.esm", "000812:Skyrim.esm"
    # PlacedNpc: a record type whose schema names both views (PlacedObject has no MajorFlags field)
    batches = (TR.three(fk1, TR.ref(SkyrimMajorRecordFlags="0", MajorFlags="0"),
                        TR.ref(SkyrimMajorRecordFlags="Compressed", MajorFlags="262144"),
                        TR.ref(SkyrimMajorRecordFlags="0", MajorFlags="0"), rtype="PlacedNpc")
               + TR.three(fk2, TR.ref(SkyrimMajorRecordFlags="0", MajorFlags="0"),
                          TR.ref(SkyrimMajorRecordFlags="1024", MajorFlags="Persistent"),
                          TR.ref(SkyrimMajorRecordFlags="0", MajorFlags="0"), rtype="PlacedNpc"))
    with tempfile.TemporaryDirectory() as td:
        emit, _ = TR.rows_of(batches, Path(td))
    got = [(c["Necessary"][0]["FormKey"][:6], c["Field"]) for c in TR.conflicts(emit)]
    ok = not any(f == fk1[:6] for f, _ in got) and [x for x in got if x[0] == fk2[:6]] == [(fk2[:6], "SkyrimMajorRecordFlags")]
    return ok, f"rows {got} (want only 000812 SkyrimMajorRecordFlags)"


def c1803_orderleads_reads_the_shared_flag_table():
    """Item 1 (merged orderleads-shared-flag-table): orderleads reads the record-flag bits from record-field-scope.json,
    so its rules file holds no copy of them that could drift."""
    import orderleads
    rules = json.loads(orderleads.RULES.read_text(encoding="utf-8"))
    own = (rules.get("maskedFlags") or {}).get("bits")
    scope = (json.loads(SCOPE_FILE.read_text(encoding="utf-8")).get("recordFlags") or {}).get("bits") or {}
    stripped = copy.deepcopy(rules)
    (stripped.get("maskedFlags") or {}).pop("bits", None)
    row = {"src": {"Field": "SkyrimMajorRecordFlags",
                   "Necessary": [{"Values": {"lost": "0", "winner": "Compressed"}}]}}
    try:
        judged = orderleads.compressed_only(row, stripped)
    except Exception as exc:  # noqa: BLE001 - a crash without its own copy is the red answer
        judged = f"{type(exc).__name__}: {exc}"
    ok = own is None and judged is True and bool(scope)
    return ok, f"orderleads-rules.json's own bits {sorted(own) if own else own}; judged without them {judged!r}"


# ------------------------------------------------------------------------------- 2. the reel's mod names

def names_instance(td: Path):
    """Plugins read from MO2 mod folders: ModA's folder names Nexus page 901 (held in the catalogue), ModB's and the
    patch's have no meta.ini (the lab's hand-installed folders)."""
    inst, store = td / "inst", td / "catalogue"
    folders = {BASE: None, OTHER: "R2 SUP - Other Mod", A: "R2 SUP - Mod A", B: "R2 SUP - Mod B",
               PATCH: "R2 SUP - Case 10 Key"}
    paths = {}
    for plugin, folder in folders.items():
        if folder is None:
            paths[plugin] = f"C:/Game/Data/{plugin}"
            continue
        d = inst / "mods" / folder
        d.mkdir(parents=True, exist_ok=True)
        (d / plugin).write_bytes(b"")
        paths[plugin] = str(d / plugin)
    (inst / "mods" / "R2 SUP - Mod A" / "meta.ini").write_text("[General]\nmodid=901\n", encoding="utf-8")
    (inst / "mods" / "R2 SUP - Other Mod" / "meta.ini").write_text("[General]\nmodid=902\n", encoding="utf-8")
    for nid, name in ((901, "Mod A On Nexus"), (902, "Other Mod On Nexus")):
        (store / "pages" / str(nid)).mkdir(parents=True)
        (store / "pages" / str(nid) / "snapshot.json").write_text(json.dumps({"modId": nid, "name": name,
                                                                             "readDate": "2026-11-01", "sourceUrl": "x"}),
                                                                 encoding="utf-8")
    return paths, store


def with_paths(arm_, paths):
    for p_ in arm_["requested"]["plugins"]:
        p_["path"] = paths.get(p_["name"], p_["path"])
    return arm_


def run_plan_cmd(bdir: Path, out: Path, extra=()):
    R.run_verify_bundle = lambda bundle, out_, copy_: ({"bundle": {"complete": True, "schemaSupported": True}}, 0, "")
    err, sink = io.StringIO(), io.StringIO()
    with contextlib.redirect_stderr(err), contextlib.redirect_stdout(sink):
        code = R.cmd_plan(R.build_parser().parse_args(["plan", "--bundle", str(bdir), "--out", str(out), *extra]))
    pres = out / "presentation.json"
    return code, err.getvalue(), (json.loads(pres.read_text(encoding="utf-8")) if pres.is_file() else None)


def c1804_report_and_reel_say_one_name():
    """Item 2, F2 (item 36: the report's names come from the bundle, the reel's from Nexus): an undeclared mod whose
    MO2 folder names a Nexus page is named the same in the report (its reader names, which the reader page and the
    figure captions read) as in the reel."""
    old = os.environ.get("HOUSECARL_NEXUS_CATALOG_DIR")
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        paths, store = names_instance(td)
        os.environ["HOUSECARL_NEXUS_CATALOG_DIR"] = str(store)
        try:
            arms = [with_paths(P.arm("baseline", [BASE, OTHER, A, B], kind="baseline"), paths),
                    with_paths(P.arm("without-a", [BASE, OTHER, B]), paths)]
            res = {(CONTACT, "baseline"): [P.row("CONTACT", B, A, f"000801:{A}")], (CONTACT, "without-a"): []}
            names = {"schema": "names-v1", "places": {}, "objects": {}, "missing": [],
                     "mods": {A: {"name": "ModA", "source": "plugin-stem"}}}
            bdir = P.make_bundle(td / "bundle", arms, res, [A], names=names)
            code, err, plan = run_plan_cmd(bdir, td / "out")
        finally:
            if old is None:
                os.environ.pop("HOUSECARL_NEXUS_CATALOG_DIR", None)
            else:
                os.environ["HOUSECARL_NEXUS_CATALOG_DIR"] = old
    if plan is None:
        return False, f"plan refused ({code}): {err[-400:]}"
    reel_name = ((plan.get("reel") or {}).get("modNames") or {}).get(A, {}).get("name")
    report_name = (((plan.get("reader") or {}).get("names") or {}).get("mods") or {}).get(A, {}).get("name")
    ok = reel_name == "Mod A On Nexus" and report_name == reel_name
    return ok, f"the reel says {reel_name!r}; the report's names say {report_name!r}"


def c1805_patch_without_nexus_id_said_by_its_plugin():
    """Item 2, F3 (item 36: "X from R2 SUP - Case NN … Key"): a supplied patch whose MO2 folder gives no Nexus id is
    said by its plugin's own name; the folder's name is never spoken with it."""
    import test_reader_rules as RR
    fk, other = f"000D01:{A}", f"009301:{B}"
    pres = RR.reel_pres()
    pred = pres["reader"]["prediction"]
    pred["record"].append({"admission": "admitted", "checkId": "geometry.object-overlap", "formKey": fk, "A": B,
                           "B": A, "kind": "OVERLAP", "other": [other], "withPatch": "open",
                           "summary": f"crossing in RoomOne (int) [mm] {fk} / {other}"})
    pres["reader"]["suppliedPatches"] = [PATCH]
    pres["media"] = [{"id": f"M-{st}", "assessed": True, "path": f"media/{st}.png",
                      "provenance": {"vignetteState": st, "captureRequest": "q1", "poseId": "p1"},
                      "subject": {"formKey": fk, "armId": "no-a" if st == "without" else "baseline"}}
                     for st in ("without", "with", "highlight")]
    names = {p: {"name": re.sub(r"\.(esp|esm|esl)$", "", p), "source": "nexus-catalogue"} for p in RR.MODS + [B]}
    names[PATCH] = {"name": "R2 SUP - Case 10 Key", "source": "mo2-folder",
                    "reason": "mod folder 'R2 SUP - Case 10 Key' has no meta.ini, so no Nexus mod id is known"}
    low = lambda *xs: {x.lower() for x in xs}  # noqa: E731
    masters = {PATCH.lower(): low(BASE, A, B), A.lower(): low(BASE), B.lower(): low(BASE)}
    search = {"pairs": [{"pair": [B, A], "hits": [], "queries": [{"tool": t} for t in
                                                                RR.POLICY["patchSearch"]["requiredTools"]]}]}
    reel = reel_plan.plan_reel(pres, {}, masters, RR.POLICY, RR.WORDS, search, names,
                               capture_results=[{"status": "captured", "formKey": fk, "request": "q1"}],
                               captures_final=True)
    said = " ".join(str(c.get("narration") or "") for c in reel.get("chapters") or [])
    stem = re.sub(r"\.(esp|esm|esl)$", "", PATCH)
    ok = stem in said and "R2 SUP - Case 10 Key" not in said
    return ok, f"status {reel.get('status')}; said {[s for s in re.split(r'(?<=\.) ', said) if stem in s or 'R2 SUP' in s]}"


# ---------------------------------------------------------------------------- 3. the patch search's contents

def search_reel(entry, known=None, supplied=()):
    """The reel on test_reader_rules' fixture with one pair entry of a recorded patch search."""
    import test_reader_rules as RR
    fk, other = f"000D01:{A}", f"009301:{B}"
    pres = RR.reel_pres()
    pres["reader"]["prediction"]["record"].append({"admission": "admitted", "checkId": "geometry.object-overlap",
                                                   "formKey": fk, "A": B, "B": A, "kind": "OVERLAP", "other": [other],
                                                   "summary": f"crossing in RoomOne (int) [mm] {fk} / {other}"})
    pres["reader"]["suppliedPatches"] = list(supplied)
    pres["media"] = [{"id": f"M-{st}", "assessed": True, "path": f"media/{st}.png",
                      "provenance": {"vignetteState": st, "captureRequest": "q1", "poseId": "p1"},
                      "subject": {"formKey": fk, "armId": "no-a" if st == "without" else "baseline"}}
                     for st in ("without", "with", "highlight")]
    names = {p: {"name": re.sub(r"\.(esp|esm|esl)$", "", p)} for p in RR.MODS + [B, PATCH]}
    search = {"pairs": [dict(entry, pair=[B, A], queries=[{"tool": t} for t in RR.POLICY["patchSearch"]["requiredTools"]])]}
    if known is not None:
        search["knownAnswer"] = known
    reel = reel_plan.plan_reel(pres, {}, {}, RR.POLICY, RR.WORDS, search, names,
                               capture_results=[{"status": "captured", "formKey": fk, "request": "q1"}],
                               captures_final=True)
    said = " ".join(str(c.get("narration") or "") for c in reel.get("chapters") or [])
    return reel, said


KNOWN = {"pair": ["Hub Mod.esp", "ModA.esp"], "patch": "Hub - ModA patch.esp",
         "hits": [{"file": "Hub Patch Collection", "contentPaths": ["Hub - ModA patch.esp"]}]}


def c1806_listed_hub_names_the_patch_inside():
    """Item 3 (item 22 F1: "a 'listed' sentence can name only the hub's file, not the patch inside it"): a hit the
    search found by a path inside a hub's archive says the hub and the plugin it holds."""
    hit = {"file": "Big Patch Collection", "pageId": 157450, "fileId": 808879, "status": "live", "category": "MAIN",
           "contentPaths": ["Options/ModB - ModA patch.esp"]}
    reel, said = search_reel({"hits": [hit], "unreadSources": []})
    line = next((s for s in re.split(r"(?<=\.) ", said) if "Big Patch Collection" in s), "")
    ok = "Big Patch Collection" in line and "ModB - ModA patch" in line
    return ok, f"the listed sentence {line!r}"


def c1807_unrecorded_listings_never_say_no_patch():
    """Item 3 (the round-2 recorder writes no `unreadSources` at all): a pair entry that does not record which sources
    went unread is not a search that read every source, so the reel never says "No patch exists" from it."""
    reel, said = search_reel({"hits": []}, known=KNOWN)
    seg = next((s for s in reel.get("chapters") or [] if "patch" in str(s.get("narration") or "").lower()), {})
    case = [((x.get("evidence") or {}).get("patch") or {}).get("case") for x in reel.get("chapters") or []]
    ok = "No patch exists" not in said and "There is no patch" not in said
    return ok, f"said {[s for s in re.split(r'(?<=\.) ', said) if 'patch' in s.lower()]}; cases {[c for c in case if c]}"


def c1808_recorder_keeps_listings_paths_and_build():
    """Item 3 (item 22 F1, three gaps): the tracked patch-search recorder's curated pair carries every search's
    `coverage.contentsUnread` row in `unreadSources`, keeps each hit's matched archive paths, and names its build by
    commit and by the sha256 of housecarl-mcp.dll and housecarl-nexus.dll (the apphost stub identifies no build)."""
    try:
        import patchsearch  # noqa: F401
    except ImportError as exc:
        return False, f"no tracked recorder: {exc}"
    answer = {"hits": [{"modId": 157450, "pageName": "Hub", "file": {"fileId": 808879, "name": "Hub Patch Collection",
                                                                    "status": "live", "category": "MAIN"},
                        "matches": [{"term": "ModA", "field": "contents", "contentPath": "Hub - ModA patch.esp"}]}],
              "coverage": {"contentsUnread": [{"modId": 266, "fileId": 38084, "reason": "not-read-yet",
                                               "sentence": "The archive listing of file 38084 has not been read yet."}]}}
    pair = patchsearch.curate_pair(["ModB.esp", "ModA.esp"], [json.dumps(answer)], json.dumps(answer), queries=[])
    with tempfile.TemporaryDirectory() as td:
        exe = Path(td) / "housecarl-mcp.exe"
        for n_ in ("housecarl-mcp.exe", "housecarl-mcp.dll", "housecarl-nexus.dll"):
            (Path(td) / n_).write_bytes(n_.encode())
        build = patchsearch.build_identity(exe)
    paths = [p_ for h in pair.get("hits") or [] for p_ in h.get("contentPaths") or []]
    unread = [u for u in pair.get("unreadSources") or [] if "38084" in json.dumps(u)]
    ok = paths == ["Hub - ModA patch.esp"] and len(unread) == 1 and {"housecarl-mcp.dll", "housecarl-nexus.dll"} <= set(
        (build.get("sha256") or {})) and "commit" in build
    return ok, f"paths {paths}; unread {unread}; build {build}"


PATCH_STUB = r'''
import json, sys
for line in sys.stdin:
    m = json.loads(line)
    if "id" not in m:
        continue
    if m["method"] == "initialize":
        res = {"protocolVersion": "2025-06-18", "capabilities": {}, "serverInfo": {"name": "stub", "version": "1"}}
    else:
        name, args = m["params"]["name"], m["params"]["arguments"]
        if name == "housecarl_nexus_catalog":
            body = {"added": args.get("add")}
        elif name == "housecarl_nexus_mod":
            body = {"mod": args.get("mod"), "note": "description truncated"}
        elif "\n" in args.get("terms", ""):
            body = {"hits": [{"modId": 157450, "pageName": "Hub", "sourceUrl": "stub",
                              "file": {"fileId": 808879, "name": "Hub Patch Collection", "status": "live",
                                       "category": "MAIN", "version": "1.6.1"},
                              "matches": [{"term": "ModA", "field": "contents", "contentPath": "opt/Hub - ModA patch.esp"}]}],
                    "coverage": {"contentsUnread": [{"modId": 266, "fileId": 38084, "reason": "not-read-yet",
                                                     "sentence": "The archive listing of file 38084 has not been read yet."}]}}
        elif args.get("terms", "").startswith("ModB"):
            body = {"hits": []}
        else:
            body = {"hits": [], "coverage": {"contentsUnread": []}}
        res = {"content": [{"type": "text", "text": json.dumps(body)}], "isError": False}
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": m["id"], "result": res}) + "\n")
    sys.stdout.flush()
'''


def c1812_recorder_never_records_unread_as_none():
    """Item 3 (D2: a missing unreadSources must never mean "everything read", by construction): the tracked recorder,
    run against a stub server, writes `unreadSources` on every pair; an answer with no readable coverage becomes a
    "not known" row; the reel on that record never says "No patch exists" even with the known answer found."""
    try:
        import patchsearch
    except ImportError as exc:
        return False, f"no tracked recorder: {exc}"
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        stub = td / "stub.py"
        stub.write_text(PATCH_STUB, encoding="utf-8")
        os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(td / "runlog"))
        sink = io.StringIO()
        with contextlib.redirect_stdout(sink):
            code = patchsearch.main(["--reader-cmd", f'"{sys.executable}" "{stub}"', "--out", str(td / "out"),
                                     "--pair", "ModA.esp=901=ModA", "--pair", "ModB.esp=902=ModB",
                                     "--known", "ModA.esp,ModB.esp=Hub - ModA patch.esp=157450"])
        doc = json.loads((td / "out" / "patch-search.json").read_text(encoding="utf-8")) if code == 0 else {}
    pair = (doc.get("pairs") or [{}])[0]
    unread = pair.get("unreadSources")
    not_known = [u for u in unread or [] if "not known" in str(u.get("why"))]
    paths = [p_ for h in pair.get("hits") or [] for p_ in h.get("contentPaths") or []]
    reel, said = search_reel({"hits": [], "unreadSources": [u for u in unread or [] if u in not_known]},
                             known=doc.get("knownAnswer") or KNOWN)
    ok = (code == 0 and isinstance(unread, list) and len(not_known) == 1 and paths == ["opt/Hub - ModA patch.esp"]
          and "No patch exists" not in said and (doc.get("build") or {}).get("sha256") is not None)
    return ok, f"exit {code}; unread {unread}; paths {paths}; said {said[-160:]!r}"


# ---------------------------------------------------------------------------------- 4. the author-advice count

def advice_doc(rules=0, clashes=1):
    return {"rules": [{"before": "X.esp", "after": "Y.esp"}] * rules,
            "clashes": [{"namedModId": 9002, "namedModName": "Beta - A Shrine Overhaul", "pageModId": 9001,
                         "page": "https://www.nexusmods.com/skyrimspecialedition/mods/9001", "readDate": "2026-11-08",
                         "line": "Not compatible with Beta - A Shrine Overhaul", "symptom": "the trapdoor is blocked"}]
            * clashes, "statements": []}


def c1809_advice_count_includes_stated_clashes():
    """Item 4 (item 23 ruling R1: the registry still counts rules): the count the evaluator takes for
    patch.author-advice, by the registry's own result spec, is not 0 for a page that states one clash and no order
    rule."""
    import evalrun
    reg = json.loads(REGISTRY.read_text(encoding="utf-8"))
    chk = next(c for c in reg["checks"] if c.get("id") == ADVICE)
    spec = (chk.get("adapter") or {}).get("result") or {}
    got, why = evalrun.resolve_spec(spec.get("findings"), advice_doc(), "")
    ok = got == 1
    return ok, f"result.findings {spec.get('findings')!r} gives {got!r} {why}"


def c1813_registry_v3_bundle_unchanged():
    """Item 4, the D4 condition: a bundle frozen at registry v3 (findings "rules") whose disposition predates the
    clash count compiles, and its author-advice line is the catalogue line it always was; the v3 registry still
    validates beside the v4 one."""
    import evalregistry
    v4 = json.loads(REGISTRY.read_text(encoding="utf-8"))
    v3 = copy.deepcopy(v4)
    v3["registryVersion"] = 3
    next(c for c in v3["checks"] if c["id"] == ADVICE)["adapter"]["result"]["findings"] = "rules"
    probs = {n: evalregistry.validate(d) for n, d in (("v3", v3), ("v4", v4))}
    f_, _ = advice_bundle(advice=[{"checkId": ADVICE, "armId": "baseline", "provenance": None, "staleness": "x"}])
    want = ("0 rows catalogued on arm baseline. A catalogue is evidence, not a defect; it completes its check and is "
            "never a finding.")
    ok = f_.get("plain") == want and not any(probs.values()) and v4.get("registryVersion") == 4
    return ok, f"the v3 bundle's line {f_.get('plain')!r}; registry problems {probs}"


def advice_bundle(advice):
    """A bundle whose disposition holds an author-advice catalogue row with no rule found and `advice` as its
    discovery.advice: (the catalogue finding, the evidence index)."""
    arms = [P.arm("baseline", [BASE, OTHER, A, B], kind="baseline"), P.arm("without-a", [BASE, OTHER, B])]
    res = {(CONTACT, "baseline"): [], (CONTACT, "without-a"): []}
    with tempfile.TemporaryDirectory() as td:
        bdir = P.make_bundle(Path(td) / "bundle", arms, res, [A])
        disp = json.loads((bdir / "disposition.json").read_text(encoding="utf-8"))
        disp["evaluation"].setdefault("completedCatalogues", []).append(
            {"checkId": ADVICE, "armId": "baseline", "count": 0, "unit": "rows", "kind": "discovery"})
        disp["discovery"] = {"advice": advice}
        (bdir / "disposition.json").write_text(json.dumps(disp), encoding="utf-8")
        reg = json.loads((bdir / "registry" / "check-registry.json").read_text(encoding="utf-8"))
        reg["checks"].append({"id": ADVICE, "family": "patch", "title": "The patch author's stated requirements",
                              "adapter": {"exe": "adviceprobe"}})
        (bdir / "registry" / "check-registry.json").write_text(json.dumps(reg), encoding="utf-8")
        pr = json.loads(P.PRIORITIES.read_text(encoding="utf-8"))
        settings = {"mode": "concise", "layout": "normal", "maxPages": 8,
                    "page": {"size": "A4", "marginMm": 16, "baseFontPt": 10.5}, "declaredPatches": [],
                    "rowsPerGroup": 3, "figuresPerFinding": 1, "reel": {}}
        plan = R.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir))
        view = R.effective_view(plan, 3, 1, pr, "normal")
        out = R.render_all(plan, view, Path(td) / "out", "")
    return next((f for f in plan.get("findings") or [] if f.get("checkId") == ADVICE), {}), out["evidence-index.md"]


def c1810_index_says_the_stated_clash():
    """Item 4: the evidence index's line for the author-advice catalogue says the clash the page states (the
    disposition's discovery.advice already records it), never a bare "0 rows"."""
    arms = [P.arm("baseline", [BASE, OTHER, A, B], kind="baseline"), P.arm("without-a", [BASE, OTHER, B])]
    res = {(CONTACT, "baseline"): [], (CONTACT, "without-a"): []}
    with tempfile.TemporaryDirectory() as td:
        bdir = P.make_bundle(Path(td) / "bundle", arms, res, [A])
        disp = json.loads((bdir / "disposition.json").read_text(encoding="utf-8"))
        disp["evaluation"].setdefault("completedCatalogues", []).append(
            {"checkId": ADVICE, "armId": "baseline", "count": 0, "unit": "rows", "kind": "discovery"})
        disp["discovery"] = {"advice": [{"checkId": ADVICE, "armId": "baseline", "rules": 0, "clashCount": 1,
                                         "clashes": advice_doc()["clashes"]}]}
        (bdir / "disposition.json").write_text(json.dumps(disp), encoding="utf-8")
        reg = json.loads((bdir / "registry" / "check-registry.json").read_text(encoding="utf-8"))
        reg["checks"].append({"id": ADVICE, "family": "patch", "title": "The patch author's stated requirements",
                              "adapter": {"exe": "adviceprobe"}})
        (bdir / "registry" / "check-registry.json").write_text(json.dumps(reg), encoding="utf-8")
        pr = json.loads(P.PRIORITIES.read_text(encoding="utf-8"))
        conc, page = pr.get("concise") or {}, pr.get("page") or {}
        settings = {"mode": "concise", "layout": "normal", "maxPages": 8,
                    "page": {"size": "A4", "marginMm": 16, "baseFontPt": 10.5}, "declaredPatches": [],
                    "rowsPerGroup": 3, "figuresPerFinding": 1, "reel": {}}
        plan = R.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir))
        view = R.effective_view(plan, 3, 1, pr, "normal")
        out = R.render_all(plan, view, Path(td) / "out", "")
    f_ = next((f for f in plan.get("findings") or [] if f.get("checkId") == ADVICE), {})
    index = out["evidence-index.md"]
    line = next((ln for ln in index.splitlines() if "catalogued" in ln and "baseline" in ln
                 and (f_.get("plain") or "\0")[:20] in ln), f_.get("plain") or "")
    ok = bool(f_) and "Beta - A Shrine Overhaul" in str(f_.get("plain")) and "0 rows" not in str(f_.get("plain"))
    return ok, f"the catalogue line {f_.get('plain')!r}"


# ------------------------------------------------------------------------ 5. media from further siblings

def c1811_reel_reads_media_of_the_sibling_holding_the_setup():
    """Item 5 (item 21: the reel reads media from the first shared-setup sibling only): where the setup without the
    evaluated mod comes from a further (sub-order) sibling, the reel is given that sibling's frames of it, and every
    frame it would take as the "without" picture is of that setup."""
    import test_evalreport_siblings as S
    T = S.T
    fk = f"000802:{A}"
    w_only = ("eval-neighbours-alone", [T.arm("baseline", [BASE, S.DEP, S.NB1, S.NB2], kind="baseline"),
                                        T.arm("dependency-only", [BASE, S.DEP])],
              {(CONTACT, "baseline"): [], (CONTACT, "dependency-only"): []}, [A], {"plan_id": "sibneighbours"})
    prim = ("eval-combined-no-nb2", [T.arm("baseline", S.BLOCK, kind="baseline"),
                                      T.arm("no-nb2", [x for x in S.BLOCK if x != S.NB2])],
            {(CONTACT, "baseline"): [S.against_vanilla(S.NB1), S.against_neighbour()],
             (CONTACT, "no-nb2"): [S.against_vanilla(S.NB1), S.against_neighbour()]}, [A], {"plan_id": "sibcombnonb2"})
    seen = {}
    real = reel_plan.plan_reel

    def spy(pres, *a, **k):
        seen["pres"] = pres
        return real(pres, *a, **k)
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        dirs = {}
        for name, arms, results, targets, kw in (S.main_spec(S.BLOCK), w_only, prim):
            dirs[name] = T.make_bundle(td / name, arms, results, targets, **kw)
        man_p = dirs[w_only[0]] / "manifest.json"
        man = json.loads(man_p.read_text(encoding="utf-8"))
        (dirs[w_only[0]] / "media").mkdir(exist_ok=True)
        (dirs[w_only[0]] / "media" / "without.png").write_bytes(b"png")
        man["evidence"].append({"id": "mwithout0000001", "kind": "media", "strength": "supplied-media",
                                "path": "media/without.png", "sha256": "9" * 64, "mediaKind": "screenshot",
                                "subject": {"formKey": fk, "armId": "baseline", "checkId": CONTACT},
                                "provenance": {"vignetteState": "without", "captureRequest": "q1", "poseId": "p1"}})
        man_p.write_text(json.dumps(man), encoding="utf-8")
        reel_plan.plan_reel = spy
        try:
            code, err, plan = run_plan_cmd(dirs["main"], td / "out",
                                           ["--declare-patch", S.PATCH, "--sibling-bundle", str(dirs[w_only[0]]),
                                            "--sibling-bundle", str(dirs[prim[0]])])
        finally:
            reel_plan.plan_reel = real
    if plan is None:
        return False, f"plan refused ({code}): {err[-400:]}"
    pred = pred_of(plan)
    w_arm = pred.get("withoutArm")
    derive = getattr(reel_plan, "without_arm_of", None)
    w_reel = derive(pred) if derive else (str(w_arm or "").split(":", 1)[-1] or None)
    media = (seen.get("pres") or {}).get("media") or []
    mine = [m for m in media if str(m.get("fromBundle") or "").endswith(w_only[0])]
    as_without = [(m.get("fromBundle"), (m.get("subject") or {}).get("armId")) for m in media
                  if (m.get("subject") or {}).get("armId") == w_reel]
    ok = (bool(mine) and bool(as_without)
          and all(str(fb or "").endswith(w_only[0]) for fb, _ in as_without))
    return ok, (f"withoutArm {w_arm!r} (the reel matches {w_reel!r}); the further sibling's frames given to the reel "
                f"{len(mine)}; frames the reel would take as 'without' {as_without}")


CASES = [
    ("C1800-compressed-only-flags", c1800_compressed_only_flags_are_no_lost_change),
    ("C1801-one-header-change-once", c1801_one_header_change_counted_once),
    ("C1802-record-rows-flags", c1802_record_rows_mask_and_dedupe_the_flags),
    ("C1803-orderleads-shared-table", c1803_orderleads_reads_the_shared_flag_table),
    ("C1804-one-name-report-and-reel", c1804_report_and_reel_say_one_name),
    ("C1805-patch-by-plugin-name", c1805_patch_without_nexus_id_said_by_its_plugin),
    ("C1806-listed-hub-names-patch", c1806_listed_hub_names_the_patch_inside),
    ("C1807-unrecorded-unread-no-none", c1807_unrecorded_listings_never_say_no_patch),
    ("C1808-recorder-contents", c1808_recorder_keeps_listings_paths_and_build),
    ("C1812-recorder-unread-by-construction", c1812_recorder_never_records_unread_as_none),
    ("C1809-advice-count-clashes", c1809_advice_count_includes_stated_clashes),
    ("C1810-index-says-clash", c1810_index_says_the_stated_clash),
    ("C1813-registry-v3-bundle", c1813_registry_v3_bundle_unchanged),
    ("C1811-sibling-media", c1811_reel_reads_media_of_the_sibling_holding_the_setup),
]


def main(argv=None) -> int:
    want = list(argv if argv is not None else sys.argv[1:])
    fails = 0
    for name, fn in CASES:
        if want and not any(name.startswith(w) for w in want):
            continue
        try:
            ok, detail = fn()
        except Exception:  # noqa: BLE001 - a crash is a failed case, with its traceback
            ok, detail = False, traceback.format_exc(limit=6)
        print(f"{'PASS' if ok else 'FAIL'} {name}: {detail}", flush=True)
        fails += 0 if ok else 1
    print(f"{fails} failed" if fails else "all passed")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
