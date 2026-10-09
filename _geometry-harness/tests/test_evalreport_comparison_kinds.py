#!/usr/bin/env python
"""Checks for how the report compiler classifies a setup and what it is then allowed to claim.

These guard two defects that reached a delivered report, and one that a review found in the
first attempt to fix them:

  * a setup that merely REMOVED a mod was described as resolving measured conflicts, which a
    reader takes as "a fix exists". Removing a mod removes that mod's conflicts by
    construction -- arithmetic, not a repair.
  * a mod's own companion master files, which differ from the baseline only because the mod
    they belong to was removed, were named as "the patches evaluated".
  * the first fix still GUESSED patch identity from plugin membership, so adding any unrelated
    plugin was read as a patch comparison; and it compared plugin membership as a SET, so
    reversing the load order was called a same-order control.

Patch identity is now declared, never inferred, and order is compared as order.

**The verdict cells below drive real findings through to the reader-facing sentence.** The
first version of this file asserted only on `comparisonKind`, over fixtures with an empty
ledger and registry -- so the repair branch was never reached and replacing the whole gate
with `return True` left all nine cells passing. `--prove-gate` re-runs the suite with the gate
forced open, in memory, and requires it to FAIL; a suite that still passes with the gate
disabled does not protect the sentence.

Usage:
  python _geometry-harness/tests/test_evalreport_comparison_kinds.py
  python _geometry-harness/tests/test_evalreport_comparison_kinds.py --prove-gate
Exit 0 all pass, 1 a cell failed.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import evalreport as R                                               # noqa: E402

TARGETS = ["ModA.esp", "ModB.esp"]
DEPS = ["Skyrim.esm", "Update.esm"]
COMPANIONS = ["ModB0.esp", "ModB1.esp"]          # ModB's own extra masters, not patches
PATCH = "ModA - ModB patch.esp"
UNRELATED = "UnrelatedQuest.esp"                 # not a patch, not a target
CHECK = "geometry.placement-contact"


def plugins(*names):
    return [{"name": n, "sha256": "0" * 64, "path": f"X:/x/{n}"} for n in names]


def arm(arm_id, names, kind="candidate", provenance=None):
    return {"armId": arm_id, "kind": kind, "candidateFile": f"{arm_id}.json",
            "provenance": provenance or {},
            "requested": {"plugins": plugins(*names), "providers": [], "overlays": [],
                          "population": "load-time"},
            "expected": {"states": ["baseline"], "population": "load-time"}}


def make_bundle(root: Path, arms, repaired_on=None, created_on=None, patches=None,
                import_note=None, import_arm=None):
    """A bundle small enough to build by hand and complete enough to reach the verdict.

    `repaired_on` / `created_on` name arms whose orderEvaluation reports a repair or a new
    defect, which is what drives the reader-facing Result sentence.
    """
    root.mkdir(parents=True, exist_ok=True)
    plan = {"planId": "test00000000", "requestProviders": "arm-view",
            "createdUtc": "2026-01-01T00:00:00+00:00",
            "targets": [{"name": t} for t in TARGETS],
            "arms": arms, "states": {"selected": ["baseline"]},
            "checks": [{"checkId": CHECK, "family": "geometry", "title": "Contact",
                        "arms": [{"armId": a["armId"], "status": "probe"} for a in arms]}]}
    if patches:
        plan["patches"] = list(patches)
    arms_disp = {}
    for a in arms:
        per = {}
        if a["armId"] in (repaired_on or []):
            per[CHECK] = {"verdict": "REPAIRED", "repaired": 3, "reclassified": 0,
                          "created": 0, "newDefect": 0, "attributed": 0, "unmeasured": 0}
        if a["armId"] in (created_on or []):
            per[CHECK] = {"verdict": "HARMFUL", "repaired": 0, "reclassified": 0,
                          "created": 2, "newDefect": 2, "attributed": 0, "unmeasured": 0}
        arms_disp[a["armId"]] = {"kind": a["kind"],
                                 "orderEvaluation": {"classification": None, "probes": [CHECK],
                                                     "regression": bool(per.get(CHECK, {}).get("created")),
                                                     "perProbe": per, "scope": "test"}}
    disp = {"evaluation": {"complete": False, "claim": "incomplete", "missing": []},
            "arms": arms_disp, "importedEvidence": [], "unassessed": {}}
    receipts = {}
    if isinstance(import_note, dict):
        # several credited imports, each with its own note
        disp["importedEvidence"] = []
        (root / "receipts").mkdir(exist_ok=True)
        for a_id, note in import_note.items():
            rid = f"{CHECK}__{a_id}"
            disp["importedEvidence"].append({"checkId": CHECK, "armId": a_id,
                                             "receiptId": rid, "origin": "reused"})
            receipts[rid] = {
                "receiptId": rid, "checkId": CHECK, "armId": a_id, "part": None,
                "schemaVersion": 1, "exitCode": 0,
                "adapter": {"kind": "mcp-import", "sourceKind": "mcp-only"},
                "validation": {"accepted": True, "identityLimits": []},
                "imported": {"origin": "reused", "capturedUtc": "2026-01-01T00:00:00+00:00",
                             "sourceSha256": "0" * 64, "reusedWithin": "another-evaluation",
                             "reusedFrom": {"planId": "src000000000", "armId": a_id,
                                            "resultSha256": "1" * 64},
                             "reusedFromVerified": False, "reusedFromNote": "declared",
                             "tool": {"name": "test"}},
                "result": {"detector": "finding", "findings": 1, "note": note,
                           "coverage": [{"Population": CHECK, "Judged": 1,
                                         "Unjudgeable": {}}]},
            }
            (root / "receipts" / f"{rid}.json").write_text(json.dumps(receipts[rid]),
                                                           encoding="utf-8")
    elif import_note:
        # A credited import whose result carries a note. This is how a corrected rendition
        # says what it is: reuse provenance states where the evidence came from, the note
        # states what was done with it. A reader shown only the first cannot tell that the
        # numbers were recomputed.
        a_id = import_arm or arms[-1]["armId"]
        rid = f"{CHECK}__{a_id}"
        disp["importedEvidence"] = [{"checkId": CHECK, "armId": a_id, "receiptId": rid,
                                     "origin": "reused"}]
        receipts[rid] = {
            "receiptId": rid, "checkId": CHECK, "armId": a_id, "part": None,
            "schemaVersion": 1, "exitCode": 0,
            "adapter": {"kind": "mcp-import", "sourceKind": "mcp-only"},
            "validation": {"accepted": True, "identityLimits": []},
            "imported": {"origin": "reused", "capturedUtc": "2026-01-01T00:00:00+00:00",
                         "sourceSha256": "0" * 64, "reusedWithin": "another-evaluation",
                         "reusedFrom": {"planId": "src000000000", "armId": a_id,
                                        "resultSha256": "1" * 64},
                         "reusedFromVerified": False,
                         "reusedFromNote": "declared, not re-checked",
                         "tool": {"name": "test"}},
            "result": {"detector": "finding", "findings": 1, "note": import_note,
                       "coverage": [{"Population": CHECK, "Judged": 1, "Unjudgeable": {}}]},
        }
        (root / "receipts").mkdir(exist_ok=True)
        (root / "receipts" / f"{rid}.json").write_text(json.dumps(receipts[rid]),
                                                       encoding="utf-8")
    (root / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
    (root / "disposition.json").write_text(json.dumps(disp), encoding="utf-8")
    (root / "ledger.json").write_text(json.dumps({"entries": []}), encoding="utf-8")
    (root / "registry").mkdir(exist_ok=True)
    (root / "registry" / "check-registry.json").write_text(json.dumps(
        {"registryVersion": 1,
         "checks": [{"checkId": CHECK, "family": "geometry", "title": "Contact"}]}),
        encoding="utf-8")
    evidence = [{"id": f"r-{rid}", "kind": "receipt", "path": f"receipts/{rid}.json",
                 "sha256": "0" * 64, "checkId": CHECK, "armId": receipts[rid]["armId"],
                 "recordedBy": rid} for rid in receipts]
    (root / "manifest.json").write_text(json.dumps({
        "bundleSchema": 2, "plan": "plan.json", "disposition": "disposition.json",
        "ledger": "ledger.json", "registry": "registry/check-registry.json",
        "evidence": evidence}), encoding="utf-8")
    return root


def run_compiler(arms, repaired_on=None, created_on=None, patches=None, declared=None,
                 import_note=None, import_arm=None):
    with tempfile.TemporaryDirectory() as td:
        bdir = make_bundle(Path(td) / "bundle", arms, repaired_on, created_on, patches,
                           import_note, import_arm)
        pr = json.loads((HERE.parent.parent / ".claude/skills/evaluation-reporting/"
                         "references/report-priorities.json").read_text(encoding="utf-8"))
        conc, page = pr.get("concise") or {}, pr.get("page") or {}
        settings = {"mode": "concise", "layout": conc.get("layout", "normal"),
                    "maxPages": conc.get("maxPages", 8),
                    "page": {"size": page.get("size", "A4"),
                             "marginMm": page.get("marginMm", 16),
                             "baseFontPt": page.get("baseFontPt", 10.5)},
                    "declaredPatches": list(declared or []),
                    "rowsPerGroup": conc.get("rowsPerGroup", 3),
                    "figuresPerFinding": conc.get("figuresPerFinding", 1),
                    "reel": {k: (pr.get("reel") or {}).get(k)
                             for k in ("maxSeconds", "fps", "width", "height")}}
        plan = R.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir))
    scope = plan.get("scope") or {}
    kinds = {a["armId"]: a.get("comparisonKind") for a in scope.get("arms") or []}
    # The comparison sentence these cells guard is the validation headline: the reader's verdict leads with the
    # prediction (what adding the mods will break) and the patch comparison follows as its answer key.
    verdict = ((((plan.get("reader") or {}).get("validation") or {}).get("headline"))
               or (plan.get("result") or {}).get("verdict") or "")
    if not verdict:
        blob = json.dumps(plan)
        for probe in ("resolves some measured conflicts", "records fewer conflicts",
                      "do not establish", "introduces a new defect"):
            if probe in blob:
                verdict = blob
                break
    return kinds, verdict, json.dumps(plan), plan


def render_artifacts(plan):
    """Render the REAL templates and return {artifact name: text}.

    A plan-level assertion checks the data the templates consume, which passes whether or not
    any given template consumes it. That is exactly how the correction disclosure reached the
    Markdown evidence index and not the HTML one: `p.imports.notes` was present and correct,
    one of two templates read it, and a test on the data could not tell. These cells assert on
    the rendered text of every artifact instead.
    """
    pr = json.loads((HERE.parent.parent / ".claude/skills/evaluation-reporting/"
                     "references/report-priorities.json").read_text(encoding="utf-8"))
    view = R.effective_view(plan, 3, 1, pr, "normal")
    css = ""
    with tempfile.TemporaryDirectory() as td:
        return R.render_all(plan, view, Path(td), css)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--prove-gate", action="store_true",
                    help="force repair_claimable open in memory and require this suite to FAIL")
    args = ap.parse_args(argv)
    if args.prove_gate:
        return prove_gate()

    cells = []

    def cell(name, ok, detail):
        cells.append((name, bool(ok), detail))

    RESOLVES = "resolves some measured conflicts"

    # --- 1. ABLATION layout, WITH a repair reported: the sentence must not claim repair ----
    ablation = [
        arm("baseline", DEPS + TARGETS + COMPANIONS, kind="baseline"),
        arm("dep-only", DEPS),
        arm("minus-b", DEPS + ["ModA.esp"]),
        arm("minus-a", DEPS + ["ModB.esp"] + COMPANIONS),
    ]
    kinds, verdict, text, _pl = run_compiler(ablation, repaired_on=["minus-b"])
    cell("ablation-kinds",
         kinds.get("dep-only") == "dependency-control"
         and kinds.get("minus-b") == "mod-removal" and kinds.get("minus-a") == "mod-removal",
         f"{ {k: v for k, v in kinds.items() if k != 'baseline'} }")
    cell("removal-with-repair-does-not-claim-repair",
         RESOLVES not in verdict and "removing content" in verdict,
         f"verdict: {verdict[:150]!r}")
    cell("companions-are-not-patches",
         "No separate patch plugin was declared" in text and "ModB0.esp, ModB1.esp," not in text,
         "a mod's companion masters must never be named as the evaluated patches")

    # --- 2. a DECLARED patch that repairs: the sentence MUST claim repair ------------------
    declared = [
        arm("baseline", DEPS + TARGETS, kind="baseline"),
        arm("with-patch", DEPS + TARGETS + [PATCH]),
    ]
    kinds2, verdict2, text2, _pl = run_compiler(declared, repaired_on=["with-patch"],
                                           patches=[PATCH])
    cell("declared-patch-is-a-patch-comparison",
         kinds2.get("with-patch") == "patch-comparison", f"{kinds2.get('with-patch')!r}")
    cell("declared-patch-repair-is-claimed",
         RESOLVES in verdict2, f"verdict: {verdict2[:150]!r}")
    cell("declared-patch-is-named", PATCH in text2, "the declared patch must be named")

    # --- 3. an UNDECLARED plugin must NOT become a patch, and must not claim repair --------
    undeclared = [
        arm("baseline", DEPS + TARGETS, kind="baseline"),
        arm("plus-unrelated", DEPS + TARGETS + [UNRELATED]),
    ]
    kinds3, verdict3, text3, _pl = run_compiler(undeclared, repaired_on=["plus-unrelated"])
    cell("undeclared-plugin-is-not-a-patch",
         kinds3.get("plus-unrelated") == "unstated-difference",
         f"{kinds3.get('plus-unrelated')!r}; plugin membership does not identify a patch")
    cell("undeclared-plugin-does-not-claim-repair",
         RESOLVES not in verdict3 and "not declared as a patch" in verdict3,
         f"verdict: {verdict3[:150]!r}")
    cell("undeclared-plugin-not-named-as-patch",
         "No separate patch plugin was declared" in text3
         and f"The patch evaluated is {UNRELATED}" not in text3,
         f"{UNRELATED} must be reported as a plugin that differs, not an evaluated patch")

    # --- 4. the SAME plugins in a different ORDER is not a control ------------------------
    reordered = [
        arm("baseline", DEPS + ["ModA.esp", "ModB.esp"], kind="baseline"),
        arm("swapped", DEPS + ["ModB.esp", "ModA.esp"]),
    ]
    kinds4, verdict4, _, _pl = run_compiler(reordered, repaired_on=["swapped"])
    cell("reorder-is-not-a-control",
         kinds4.get("swapped") == "reordered-alternative",
         f"{kinds4.get('swapped')!r}; a load order is ordered and reordering changes winners")
    cell("reorder-does-not-claim-repair",
         RESOLVES not in verdict4 and "different load order" in verdict4,
         f"verdict: {verdict4[:150]!r}")

    # --- 5. identical ORDER really is a control ------------------------------------------
    same = [
        arm("baseline", DEPS + TARGETS, kind="baseline"),
        arm("control", DEPS + TARGETS),
    ]
    kinds5, _, _, _pl = run_compiler(same)
    cell("identical-order-is-a-control",
         kinds5.get("control") == "same-order-control", f"{kinds5.get('control')!r}")

    # --- 6. mixed: a declared patch alongside a removal arm ------------------------------
    mixed = [
        arm("baseline", DEPS + TARGETS + [PATCH], kind="baseline"),
        arm("without-patch", DEPS + TARGETS),
        arm("minus-a", DEPS + ["ModB.esp"] + [PATCH]),
    ]
    kinds6, verdict6, _, _pl = run_compiler(mixed, repaired_on=["minus-a"], patches=[PATCH])
    cell("inverted-declared-patch",
         kinds6.get("without-patch") == "patch-comparison",
         f"{kinds6.get('without-patch')!r}; the patch sits in the measured order and the "
         f"alternative removes it")
    # A NEGATIVE assertion alone is what let a false sentence ship. "RESOLVES not in verdict"
    # passes for every wrong sentence as well as every right one, and the verdict went on to
    # deny that any patch setup was compared -- in a plan whose baseline carries the declared
    # patch and whose `without-patch` arm removes exactly it. Assert what the sentence must
    # SAY, given what this fixture is.
    cell("repair-from-a-removal-arm-is-not-claimed",
         RESOLVES not in verdict6,
         f"the repair came from the removal arm, not the patch arm: {verdict6[:110]!r}")
    cell("mixed-plan-does-not-deny-an-existing-patch-comparison",
         "No setup containing a patch was compared" not in verdict6
         and PATCH in verdict6 and "was also compared" in verdict6,
         f"a declared patch comparison exists in this plan, so the verdict must not deny one "
         f"and must say what happened to it: {verdict6[:220]!r}")
    # ... and the denial must still appear when it is TRUE, or the fix has simply deleted it.
    kinds6b, verdict6b, _, _pl = run_compiler(
        [arm("baseline", DEPS + TARGETS + COMPANIONS, kind="baseline"),
         arm("minus-b", DEPS + ["ModA.esp"])],
        repaired_on=["minus-b"])
    cell("no-patch-arm-still-says-none-was-compared",
         "No setup containing a patch was compared" in verdict6b,
         f"with no patch arm anywhere the denial is accurate and must survive: "
         f"{verdict6b[:160]!r}")

    # --- 7. --declare-patch reaches the classifier ----------------------------------------
    kinds7, verdict7, _, _pl = run_compiler(undeclared, repaired_on=["plus-unrelated"],
                                       declared=[UNRELATED])
    cell("declare-patch-setting-is-honoured",
         kinds7.get("plus-unrelated") == "patch-comparison" and RESOLVES in verdict7,
         f"declaring it makes it a patch comparison: {kinds7.get('plus-unrelated')!r}")

    # --- 8. an import's note must reach the READER, not only the receipt ------------------
    NOTE = ("CORRECTED RENDITION. Recomputed under corrected rules after the patch was "
            "inspected.")
    _k8, _v8, text8, plan8 = run_compiler(
        [arm("baseline", DEPS + TARGETS, kind="baseline"),
         arm("with-patch", DEPS + TARGETS + [PATCH])],
        patches=[PATCH], import_note=NOTE, import_arm="with-patch")
    reader8 = (plan8.get("reader") or {})
    limits8 = " ".join(reader8.get("otherLimits") or [])
    cell("import-note-reaches-the-reader",
         NOTE in limits8,
         f"an import that says what it is must say it on the page, not only in the receipt; "
         f"reader.otherLimits carries it: {NOTE in limits8}")
    cell("import-note-available-to-the-index",
         NOTE in ((plan8.get("imports") or {}).get("notes") or []),
         f"the plan carries the note for the templates to render: "
         f"{(plan8.get('imports') or {}).get('notes')}")
    # ... and it must actually appear in EVERY rendered artifact that documents the imports.
    # The note reached evidence-index.md and not evidence-index.html because only one of the
    # two templates was updated, and the plan-level cell above could not see the difference.
    arts8 = render_artifacts(plan8)
    missing_in = [name for name in ("report.md", "report.html",
                                    "evidence-index.md", "evidence-index.html")
                  if NOTE not in arts8[name]]
    cell("import-note-in-every-rendered-artifact",
         not missing_in,
         f"rendered artifacts carrying the note: "
         f"{[n for n in arts8 if NOTE in arts8[n]]}"
         + (f"; MISSING FROM {missing_in}" if missing_in else ""))
    # Several imports whose notes share an explanation but differ in their figures must yield
    # ONE reader bullet, not one per import. Twelve near-identical 900-character paragraphs is
    # what the naive whole-string dedup produced on the real run.
    SHARED = "CORRECTED RENDITION. Recomputed under corrected rules."
    # A sentence every note shares but which sits AFTER the differing figures. A prefix-based
    # rule drops it; the reader loses the disclosure that matters most.
    TRAILER = "This correction was made AFTER the subject was inspected."
    _k10, _v10, _t10, plan10 = run_compiler(
        [arm("baseline", DEPS + TARGETS, kind="baseline"),
         arm("with-patch", DEPS + TARGETS + [PATCH]),
         arm("other", DEPS + TARGETS + [PATCH, UNRELATED])],
        patches=[PATCH],
        import_note={"with-patch": SHARED + " This check counted 5 (was 7). " + TRAILER,
                     "other": SHARED + " This check counted 9 (was 11). " + TRAILER})
    assert len((plan10.get("imports") or {}).get("notes") or []) == 2,         "the fixture must really carry two distinct notes"
    lim10 = [x for x in (plan10.get("reader") or {}).get("otherLimits") or []
             if "carry a note" in x]
    cell("shared-note-explanation-appears-once",
         len(lim10) == 1 and SHARED in lim10[0] and "evidence index" in lim10[0],
         f"{len(lim10)} reader bullet(s) for notes sharing an explanation; the per-import "
         f"figures go to the index")
    cell("shared-sentence-after-the-difference-survives",
         bool(lim10) and TRAILER in lim10[0],
         f"a sentence every note shares must reach the page even when it sits after the "
         f"figures that differ: {TRAILER in (lim10[0] if lim10 else '')}")

    # ... and it must appear ONCE, not once per import, and never invented when absent.
    _k9, _v9, _t9, plan9 = run_compiler(
        [arm("baseline", DEPS + TARGETS, kind="baseline"),
         arm("with-patch", DEPS + TARGETS + [PATCH])], patches=[PATCH])
    cell("no-note-invents-no-disclosure",
         not ((plan9.get("imports") or {}).get("notes"))
         and NOTE not in " ".join((plan9.get("reader") or {}).get("otherLimits") or []),
         "an evaluation whose imports carry no note gets no note sentence")
    arts9 = render_artifacts(plan9)
    leaked = [name for name, text in arts9.items()
              if "Notes the importing tool recorded" in text]
    cell("no-note-renders-no-notes-block",
         not leaked,
         f"with no note, no artifact may render the notes heading"
         + (f"; LEAKED INTO {leaked}" if leaked else ""))

    for name, ok, detail in cells:
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    failed = [n for n, ok, _ in cells if not ok]
    print(f"\n{len(cells) - len(failed)}/{len(cells)} cells passed")
    if failed:
        print("failed: " + ", ".join(failed))
    return 1 if failed else 0


def prove_gate():
    """Force the repair gate open in memory and require this suite to FAIL.

    This is the control the first version of this file lacked. Its fixtures never reached the
    verdict, so the gate could be replaced wholesale and every cell still passed. If the suite
    passes here, it is not protecting the sentence and must not be reported as if it were.
    """
    script = (
        "import sys, json, types\n"
        f"sys.path.insert(0, {str(HERE.parent)!r})\n"
        f"sys.path.insert(0, {str(HERE)!r})\n"
        "import evalreport as R\n"
        "src = open(R.__file__, encoding='utf-8').read()\n"
        "needle = '        return any(comparison_kinds.get(a) == \"patch-comparison\" for a in ids)'\n"
        "assert src.count(needle) == 1, 'gate anchor not found: %d' % src.count(needle)\n"
        "src = src.replace(needle, '        return True')\n"
        "mod = types.ModuleType('evalreport'); mod.__file__ = R.__file__\n"
        "sys.modules['evalreport'] = mod\n"
        "exec(compile(src, R.__file__, 'exec'), mod.__dict__)\n"
        "import test_evalreport_comparison_kinds as T\n"
        "T.R = mod\n"
        "sys.exit(T.main([]))\n")
    proc = subprocess.run([sys.executable, "-B", "-c", script],
                          capture_output=True, text=True, cwd=str(HERE))
    print(proc.stdout[-2500:] or proc.stderr[-2000:])
    if proc.returncode == 0:
        print("\nGATE NOT PROTECTED: the suite passed with repair_claimable forced open. "
              "These cells do not protect the repair sentence.", file=sys.stderr)
        return 1
    print(f"\ngate proven: with repair_claimable forced open the suite FAILS "
          f"(exit {proc.returncode}), so the cells are coupled to it")
    return 0


if __name__ == "__main__":
    sys.exit(main())
