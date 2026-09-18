#!/usr/bin/env python
"""Checks for how the report compiler classifies an alternative setup.

The defect these guard against produced two wrong statements in a delivered report:

  * a setup that merely REMOVED a mod was described as resolving measured conflicts, which
    a reader takes as "a fix exists". Removing a mod removes that mod's conflicts by
    construction -- that is arithmetic, not a repair.
  * a mod's own companion master files, which differ from the baseline only because the mod
    they belong to was removed, were named to the reader as "the patches evaluated".

Both came from one concept doing three jobs: "the non-target plugin that differs". The fix
classifies each arm by WHAT KIND of comparison it is, and only a patch comparison may carry
a repair reading.

These cells drive the real `compile_plan` over a minimal on-disk bundle, so they fail if the
classification is removed -- not a local re-implementation of the rule.

Usage: python _geometry-harness/tests/test_evalreport_comparison_kinds.py
Exit 0 all pass, 1 a cell failed.
"""
from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import evalreport as R                                               # noqa: E402

TARGETS = ["ModA.esp", "ModB.esp"]
DEPS = ["Skyrim.esm", "Update.esm"]
# ModB ships companion master files. They are NOT patches; they travel with ModB.
COMPANIONS = ["ModB0.esp", "ModB1.esp"]
PATCH = "ModA - ModB patch.esp"


def plugins(*names):
    return [{"name": n, "sha256": "0" * 64, "path": f"X:/x/{n}"} for n in names]


def arm(arm_id, names, kind="candidate"):
    return {"armId": arm_id, "kind": kind, "candidateFile": f"{arm_id}.json",
            "requested": {"plugins": plugins(*names), "providers": [], "overlays": [],
                          "population": "load-time"},
            "expected": {"states": ["baseline"], "population": "load-time"}}


def make_bundle(root: Path, arms):
    """The smallest bundle shape compile_plan will read."""
    root.mkdir(parents=True, exist_ok=True)
    plan = {"planId": "test00000000", "requestProviders": "arm-view",
            "createdUtc": "2026-01-01T00:00:00+00:00",
            "targets": [{"name": t} for t in TARGETS],
            "arms": arms, "checks": [], "states": {"selected": ["baseline"]}}
    disp = {"evaluation": {"complete": False, "claim": "incomplete", "missing": []},
            "arms": {a["armId"]: {"kind": a["kind"]} for a in arms},
            "importedEvidence": [], "unassessed": {}}
    (root / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
    (root / "disposition.json").write_text(json.dumps(disp), encoding="utf-8")
    (root / "ledger.json").write_text(json.dumps({"entries": []}), encoding="utf-8")
    (root / "registry").mkdir(exist_ok=True)
    (root / "registry" / "check-registry.json").write_text(
        json.dumps({"registryVersion": 1, "checks": []}), encoding="utf-8")
    (root / "manifest.json").write_text(json.dumps({
        "bundleSchema": 2, "plan": "plan.json", "disposition": "disposition.json",
        "ledger": "ledger.json", "registry": "registry/check-registry.json",
        "evidence": []}), encoding="utf-8")
    return root


def kinds_and_supplied(arms):
    """Run the REAL compile_plan and read back what it decided."""
    with tempfile.TemporaryDirectory() as td:
        bdir = make_bundle(Path(td) / "bundle", arms)
        pr = json.loads((HERE.parent.parent / ".claude/skills/evaluation-reporting/"
                         "references/report-priorities.json").read_text(encoding="utf-8"))
        conc, page = pr.get("concise") or {}, pr.get("page") or {}
        settings = {"mode": "concise", "layout": conc.get("layout", "normal"),
                    "maxPages": conc.get("maxPages", 8),
                    "page": {"size": page.get("size", "A4"),
                             "marginMm": page.get("marginMm", 16),
                             "baseFontPt": page.get("baseFontPt", 10.5)},
                    "rowsPerGroup": conc.get("rowsPerGroup", 3),
                    "figuresPerFinding": conc.get("figuresPerFinding", 1),
                    "reel": {k: (pr.get("reel") or {}).get(k)
                             for k in ("maxSeconds", "fps", "width", "height")}}
        plan = R.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir))
    scope = plan.get("scope") or {}
    got = {a["armId"]: a.get("comparisonKind") for a in scope.get("arms") or []}
    text = json.dumps(plan)
    return got, text, scope


def main():
    cells = []

    def cell(name, ok, detail):
        cells.append((name, bool(ok), detail))

    # --- layout 1: the ABLATION layout. Baseline has both mods; alternatives remove them. --
    ablation = [
        arm("baseline", DEPS + TARGETS + COMPANIONS, kind="baseline"),
        arm("dep-only", DEPS),                                    # removes BOTH targets
        arm("minus-b", DEPS + ["ModA.esp"]),                      # removes ModB + companions
        arm("minus-a", DEPS + ["ModB.esp"] + COMPANIONS),         # removes ModA only
    ]
    kinds, text, _ = kinds_and_supplied(ablation)
    cell("ablation-dependency-control", kinds.get("dep-only") == "dependency-control",
         f"dep-only -> {kinds.get('dep-only')!r} (removes every evaluated mod)")
    cell("ablation-mod-removal",
         kinds.get("minus-b") == "mod-removal" and kinds.get("minus-a") == "mod-removal",
         f"minus-b -> {kinds.get('minus-b')!r}, minus-a -> {kinds.get('minus-a')!r}")
    # the companion masters must NOT be reported as patches
    cell("companions-are-not-patches",
         "No separate patch plugin was included in the comparison" in text
         and "ModB0.esp, ModB1.esp, " not in text.replace("\\", ""),
         "a mod's companion masters travel with the mod; naming them as the evaluated "
         "patches is the mislabel this guards")
    # And a reduction measured here must not be readable as a repair. The verdict gate is
    # `repair_claimable`, which asks whether ANY arm carrying a reduction is a patch
    # comparison -- so the discriminating assertion is that no arm in this layout is one.
    # Asserting on the verdict STRING instead would be vacuous in a fixture with no findings:
    # the repair branch is never reached, so the sentence is absent whatever the code does.
    cell("removal-layout-cannot-claim-repair",
         set(kinds.values()) == {"baseline", "dependency-control", "mod-removal"}
         and "patch-comparison" not in set(kinds.values()),
         f"kinds present: {sorted(set(kinds.values()))}; none is a patch comparison, so a "
         f"reduction in this layout cannot reach the repair sentence")

    # --- layout 2: an ADDED patch. Baseline has both mods; the alternative adds a patch. ---
    added = [
        arm("baseline", DEPS + TARGETS, kind="baseline"),
        arm("with-patch", DEPS + TARGETS + [PATCH]),
    ]
    kinds2, text2, _ = kinds_and_supplied(added)
    cell("added-patch-is-a-patch-comparison",
         kinds2.get("with-patch") == "patch-comparison",
         f"with-patch -> {kinds2.get('with-patch')!r}")
    cell("added-patch-is-named",
         PATCH in text2 and "No separate patch plugin" not in text2,
         f"the added patch must be named to the reader")

    # --- layout 3: the INVERTED patched-phase layout. Baseline HAS the patch; the -------
    #     alternative removes it. This is still a patch comparison, in reverse.
    inverted = [
        arm("baseline", DEPS + TARGETS + [PATCH], kind="baseline"),
        arm("without-patch", DEPS + TARGETS),
    ]
    kinds3, text3, _ = kinds_and_supplied(inverted)
    cell("inverted-patch-is-a-patch-comparison",
         kinds3.get("without-patch") == "patch-comparison",
         f"without-patch -> {kinds3.get('without-patch')!r}; the patch sits in the measured "
         f"order and the alternative removes it")
    cell("inverted-patch-is-named",
         PATCH in text3 and "No separate patch plugin" not in text3,
         "the patch must still be named when the comparison runs in reverse")

    # --- layout 4: a same-order control ------------------------------------------------
    same = [
        arm("baseline", DEPS + TARGETS, kind="baseline"),
        arm("control", DEPS + TARGETS),
    ]
    kinds4, _, _ = kinds_and_supplied(same)
    cell("same-order-control",
         kinds4.get("control") == "same-order-control",
         f"control -> {kinds4.get('control')!r}; an identical plugin set is a comparability "
         f"control, not a change")

    for name, ok, detail in cells:
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    failed = [n for n, ok, _ in cells if not ok]
    print(f"\n{len(cells) - len(failed)}/{len(cells)} cells passed")
    if failed:
        print("failed: " + ", ".join(failed))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
