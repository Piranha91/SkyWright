"""Dump the hand-derived oracle JSONs (one per case). The values are typed by hand from PLAN.md D1-D8;
this script only writes them out so the eleven files stay consistent in shape."""
import json
from pathlib import Path

OUT = Path(__file__).resolve().parent

GEO = ["geometry.navmesh-ownership", "geometry.object-overlap", "geometry.placement-contact"]
ALL_CHECKS = ["loadability.masters-and-order", "loadability.dangling-references", "records.cell-attributes",
              *GEO, "geometry.bos-overlay", "functional.linked-dependencies",
              "state.switch-census", "state.conditional-geometry",
              "functional.collective-route-connectivity", "environment.generated-output-regeneration",
              "patch.author-advice", "patch.order-evaluation", "runtime.supplied-observations"]

# Applicability, derived from the fixture scope.json (complete; NAVM/REFR/CELL/QUST; placements interior and
# exterior; masters; a nexusId; `overlays` named under `unmeasured`) against each check's own trigger in the
# registry this fixture registry is trimmed from.
APPLIC = {
    "loadability.masters-and-order": "yes",            # {"always": true}
    "loadability.dangling-references": "yes",          # {"masters": true} - the target declares masters
    "records.cell-attributes": "yes",                  # {"anyType": ["CELL"]}
    "geometry.navmesh-ownership": "yes",               # {"anyOf": [{"navmesh": true}, {"placed": true}]}
    "geometry.object-overlap": "yes",                  # {"placed": true}
    "geometry.placement-contact": "yes",               # {"placed": true}
    "geometry.bos-overlay": "no",                      # {"overlays": true} reads configuration.overlays - empty (A-1)
    "functional.linked-dependencies": "unknown",       # dependsOnExtents: script-placement, alias-fill, bos-swaps,
                                                       # asset-replacers - all four are in scope.unmeasured (A-1)
    "state.switch-census": "yes",                      # {"placed": true}
    "state.conditional-geometry": "yes",               # {"placed": true}
    "functional.collective-route-connectivity": "yes", # {"anyOf": [{"navmesh": true}, {"placed": true}]}
    "environment.generated-output-regeneration": "yes",# {"placed": true}
    "patch.author-advice": "yes",                      # {"nexusId": true}
    "patch.order-evaluation": "yes",                   # {"always": true}
    "runtime.supplied-observations": "yes",            # {"always": true}; required: false
}
# The statuses that come from the adapter kind, not from the run (D3's status vocabulary; Q-2).
KIND_STATUS = {
    "geometry.bos-overlay": "not-applicable",          # A-2: applicability `no` wins first
    "loadability.dangling-references": "mcp-only", "records.cell-attributes": "mcp-only",
    "functional.collective-route-connectivity": "unsupported",
    "environment.generated-output-regeneration": "manual",
}
# Required checks no fixture case can ever complete: three families have no runnable adapter.
NEVER_COMPLETE = ["loadability.dangling-references", "records.cell-attributes",
                  "functional.collective-route-connectivity", "environment.generated-output-regeneration",
                  "functional.linked-dependencies"]   # A-2/Q-7: unknown is listed under missing as well
OPTIONAL = ["runtime.supplied-observations"]


def plan(**status):
    """Every check's applicability (fixed by the scope) plus a status per check."""
    out = {}
    for c in ALL_CHECKS:
        st = status.get(c.replace(".", "_").replace("-", "_"), KIND_STATUS.get(c, "planned"))
        out[c] = {"applicability": APPLIC[c], "status": st}
    if "runtime_supplied_observations" in status:
        out["runtime.supplied-observations"]["status"] = status["runtime_supplied_observations"]
    return {"checks": out}


def case(name, scenario, planned, ledger, disposition, verify=None, notes=None):
    return {"case": name, "scenario": scenario, "plan": planned, "ledger": ledger,
            "disposition": disposition, "verifyBundleExit": verify, "notes": notes or []}


cases = []

# ---------------------------------------------------------------- 01
led = {}
for c in GEO:
    led[f"{c}__baseline__arm"] = {"execution": "completed", "detector": "finding", "rejection": None}
    led[f"{c}__candidate-01-reorder__arm"] = {"execution": "completed", "detector": "finding", "rejection": None}
    led[f"{c}__candidate-01-reorder__reverse"] = {"execution": "completed", "detector": None, "rejection": None}
    led[f"{c}__candidate-01-reorder__diff"] = {"execution": "completed", "detector": None, "rejection": None}
led["loadability.masters-and-order__baseline"] = {"execution": "completed", "detector": None, "rejection": None}
led["state.switch-census__baseline"] = {"execution": "completed", "detector": "catalogue", "rejection": None}
led["patch.author-advice__baseline"] = {"execution": "completed", "detector": "catalogue", "rejection": None}
# A-8(5): a differential of the baseline against itself is not a comparison, so the derived order
# check is not-applicable on the baseline arm and must complete on every candidate arm.
led["patch.order-evaluation__baseline"] = {"execution": "not-applicable", "detector": None, "rejection": None}
led["patch.order-evaluation__candidate-01-reorder"] = {"execution": "completed", "detector": None,
                                                       "rejection": None}
led["loadability.dangling-references__baseline"] = {"execution": "mcp-only", "detector": None, "rejection": None}
led["records.cell-attributes__baseline"] = {"execution": "mcp-only", "detector": None, "rejection": None}
led["geometry.bos-overlay__baseline"] = {"execution": "not-applicable", "detector": None, "rejection": None}
led["functional.collective-route-connectivity__baseline"] = {"execution": "unsupported", "detector": None, "rejection": None}
cases.append(case(
    "case-01-geometry-and-quest",
    "A target carrying NAVM/REFR/CELL/QUST, interior and exterior placements, masters and a nexusId; scope complete "
    "with `overlays` under unmeasured. Arms baseline + candidate-01-reorder; every fake probe ok; the candidate arm "
    "emits exactly the baseline's rows.",
    plan(),
    led,
    {"complete": False, "claim": "incomplete",
     "missingIncludes": NEVER_COMPLETE,
     "missingExcludes": ["loadability.masters-and-order", *GEO, "geometry.bos-overlay",
                         "state.switch-census", "patch.order-evaluation"],
     "unknownApplicability": ["functional.linked-dependencies"],
     "arms": {"candidate-01-reorder": {"classification": "PASSES-COMPLETED-CHECKS", "regression": False,
                                       "probes": ["navownprobe", "overlapprobe", "contactprobe"]}},
     "statesNotExploredNonEmpty": None},
    verify=0,
    notes=["D7: unsupported/mcp-only/manual keep complete false and are listed under missing.",
           "D1: unknown never becomes no; listed under unknownApplicability.",
           "conflictdiff verdict on each candidate arm: DIFFERENTIAL-INERT (identical rows, non-empty config delta).",
           "PLAN A-6: a census or discovery listing is detector `catalogue` - it satisfies completion, is listed "
           "under evaluation.completedCatalogues, and is never a finding or a null check."]))

# ---------------------------------------------------------------- 02
p = plan(state_switch_census="skipped-user", state_conditional_geometry="skipped-user",
         functional_linked_dependencies="skipped-user", patch_order_evaluation="skipped-user",
         patch_author_advice="skipped-user", runtime_supplied_observations="skipped-user")
cases.append(case(
    "case-02-select-three-probes",
    "Case 01's fixtures, planned with --select navownprobe,overlapprobe,contactprobe - the packet's own "
    "wording. A-3 expands the exe names to check ids and A-8 pulls in their transitive prerequisite.",
    p,
    {f"{c}__baseline__arm": {"execution": "completed", "detector": "finding", "rejection": None} for c in GEO},
    {"complete": False, "claim": "incomplete",
     "missingIncludes": ["state.switch-census", "state.conditional-geometry",
                         "patch.order-evaluation", "patch.author-advice", *NEVER_COMPLETE],
     "missingExcludes": [*GEO, "loadability.masters-and-order", "geometry.bos-overlay"],
     "unknownApplicability": ["functional.linked-dependencies"],
     "arms": {"candidate-01-reorder": {"classification": "PASSES-COMPLETED-CHECKS", "regression": False,
                                       "probes": ["navownprobe", "overlapprobe", "contactprobe"]}},
     "explicitDecisionsMention": GEO},
    verify=0,
    notes=["D3: --select narrows what RUNS, never what is PLANNED; a required check not selected is skipped-user.",
           "Q-2: adapter-derived statuses (mcp-only/manual/unsupported) win over skipped-user.",
           "Q-3: an optional unselected check is skipped-user but not missing.",
           "A-8/Q-8: --select implies the transitive prerequisites of the selected checks, recorded as "
           "implied-by:<check>, so loadability.masters-and-order runs and is not missing."]))

# ---------------------------------------------------------------- 03
led = {}
for arm in ("baseline", "candidate-01-reorder"):
    led[f"geometry.navmesh-ownership__{arm}__arm"] = {"execution": "completed", "detector": "no-finding", "rejection": None}
    led[f"geometry.object-overlap__{arm}__arm"] = {"execution": "rejected(extraction-failed)", "detector": None,
                                                      "rejection": "extraction-failed"}
    led[f"geometry.placement-contact__{arm}__arm"] = {"execution": "rejected(extraction-failed)", "detector": None,
                                                    "rejection": "extraction-failed"}
cases.append(case(
    "case-03-null-and-extraction",
    "navownprobe emits zero conflicts with judged 42 / unjudgeable 0; overlapprobe emits zero conflicts with judged 0 "
    "and unjudgeable 17; contactprobe emits zero conflicts with every population judged 0.",
    plan(),
    led,
    {"complete": False, "claim": "incomplete",
     "missingIncludes": ["geometry.object-overlap", "geometry.placement-contact", *NEVER_COMPLETE],
     "missingExcludes": ["geometry.navmesh-ownership"],
     "unknownApplicability": ["functional.linked-dependencies"],
     "arms": {"candidate-01-reorder": {"classification": "PASSES-COMPLETED-CHECKS", "regression": False,
                                       "probes": ["navownprobe", "overlapprobe", "contactprobe"]}}},
    verify=0,
    notes=["D4: empty Conflicts with the primary population judged > 0 is no-finding.",
           "D4: primary population judged 0 while unjudgeable > 0, or every population judged 0, is extraction-failed.",
           "The sweep's own PASSES-COMPLETED-CHECKS is retained verbatim and does not make the evaluation complete."]))

# ---------------------------------------------------------------- 04
led = {
    "state.switch-census__baseline": {"execution": "rejected(exit-nonzero)", "detector": None,
                                      "rejection": "exit-nonzero"},
    # A-8(9): the status names the thing (blocked(<what>)); `blocked-prerequisite` is a receipt's
    # rejection cause, not a status word.
    "state.conditional-geometry__baseline": {"execution": "blocked(prerequisite:state.switch-census)",
                                             "detector": None, "rejection": None},
    "geometry.object-overlap__baseline__arm": {"execution": "completed", "detector": "finding", "rejection": None},
}
cases.append(case(
    "case-04-blocked-and-budget",
    "Two candidates planned with --max-candidates 1 and --exclude geometry.placement-contact=not-relevant-to-this-review; "
    "switchprobe exits 4, so state.conditional-geometry loses its prerequisite.",
    plan(geometry_placement_contact="excluded"),
    led,
    {"complete": False, "claim": "incomplete",
     "missingIncludes": ["state.switch-census", "state.conditional-geometry", "geometry.placement-contact",
                         *NEVER_COMPLETE],
     "missingExcludes": ["geometry.object-overlap", "geometry.navmesh-ownership"],
     "unknownApplicability": ["functional.linked-dependencies"],
     "maxCandidates": 1,
     "candidatesHeldBackMentions": "candidate-02-reorder-b",
     "skippedBudgetArm": "candidate-02-reorder-b",
     "explicitDecisionsMention": ["not-relevant-to-this-review"]},
    verify=0,
    notes=["D4: exit-nonzero, blocked-prerequisite.",
           "D7: unassessed lists candidates held back; D8: the bundle is written while the evaluation is incomplete.",
           "A-4: arms beyond --max-candidates N are held back and their per-arm checks are skipped-budget."]))

# ---------------------------------------------------------------- 05
led = {
    "geometry.navmesh-ownership__candidate-01-stale__arm": {"execution": "rejected(output-stale)", "detector": None,
                                                            "rejection": "output-stale"},
    "geometry.object-overlap__candidate-01-stale__arm": {"execution": "rejected(population-mismatch)",
                                                            "detector": None, "rejection": "population-mismatch"},
    "geometry.placement-contact__candidate-01-stale__arm": {"execution": "rejected(illegal-configuration)",
                                                          "detector": None, "rejection": "illegal-configuration"},
    "geometry.navmesh-ownership__candidate-02-broken__arm": {"execution": "rejected(output-missing)", "detector": None,
                                                             "rejection": "output-missing"},
    "geometry.object-overlap__candidate-02-broken__arm": {"execution": "rejected(wrong-check)", "detector": None,
                                                             "rejection": "wrong-check"},
    "geometry.placement-contact__candidate-02-broken__arm": {"execution": "rejected(identity-missing)", "detector": None,
                                                           "rejection": "identity-missing"},
}
cases.append(case(
    "case-05-rejections",
    "Baseline arms ok. candidate-01-stale: stale-output / wrong-state / illegal. candidate-02-broken: no-emit / "
    "wrong-probe-name / no-configuration.",
    plan(),
    led,
    {"complete": False, "claim": "incomplete",
     "missingIncludes": [*GEO, *NEVER_COMPLETE],
     "missingExcludes": [],
     "unknownApplicability": ["functional.linked-dependencies"],
     "arms": {}},
    verify=0,
    notes=["D4's closed rejection vocabulary, one cause per receipt.",
           "The arms' ordersweep classifications are deliberately not asserted: a stale emit is invisible to "
           "ordersweep by construction, which is why the receipt layer exists."]))

# ---------------------------------------------------------------- 06
cases.append(case(
    "case-06-attribution",
    "navownprobe's baseline row (Physical Holds) leaves the conflict set on candidate-01-sham, and the candidate emit's "
    "Predicates re-measure it as still Holds - the patch took the record over without changing the geometry.",
    plan(),
    {"geometry.navmesh-ownership__baseline__arm": {"execution": "completed", "detector": "finding", "rejection": None},
     "geometry.navmesh-ownership__candidate-01-sham__arm": {"execution": "completed", "detector": "no-finding",
                                                            "rejection": None}},
    {"complete": False, "claim": "incomplete",
     "missingIncludes": NEVER_COMPLETE, "missingExcludes": [*GEO],
     "unknownApplicability": ["functional.linked-dependencies"],
     "arms": {"candidate-01-sham": {"classification": "PASSES-COMPLETED-CHECKS", "regression": False,
                                    "probes": ["navownprobe", "overlapprobe", "contactprobe"]}},
     "armCounts": {"candidate-01-sham": {"navownprobe": {"repaired": 0, "reclassified": 1, "created": 0}}},
     "diffVerdicts": {"candidate-01-sham": {"navownprobe": "DIFFERENTIAL-RECLASSIFIED"}}},
    verify=0,
    notes=["DetectorModel.Credit(inBase, Holds, !inChanged, Holds) -> Reclassified; DifferentialVerdict(0,0,1,0,0) "
           "-> DIFFERENTIAL-RECLASSIFIED.",
           "No repair is credited anywhere on the arm: repaired == 0."]))

# ---------------------------------------------------------------- 07
cases.append(case(
    "case-07-combined-regression",
    "PatchA repairs R1 alone; PatchB repairs R2 alone; the supplied combination repairs both and creates R3, whose "
    "predicate is Gone under the baseline order (a new physical defect).",
    plan(),
    {"geometry.navmesh-ownership__candidate-03-combined__arm": {"execution": "completed", "detector": "finding",
                                                                "rejection": None}},
    {"complete": False, "claim": "incomplete",
     "missingIncludes": NEVER_COMPLETE, "missingExcludes": [*GEO],
     "unknownApplicability": ["functional.linked-dependencies"],
     "arms": {
         "candidate-01-patcha": {"classification": "PASSES-COMPLETED-CHECKS", "regression": False,
                                 "probes": ["navownprobe", "overlapprobe", "contactprobe"]},
         "candidate-02-patchb": {"classification": "PASSES-COMPLETED-CHECKS", "regression": False,
                                 "probes": ["navownprobe", "overlapprobe", "contactprobe"]},
         "candidate-03-combined": {"classification": "REGRESSION", "regression": True,
                                   "probes": ["navownprobe", "overlapprobe", "contactprobe"]}},
     "armCounts": {"candidate-03-combined": {"navownprobe": {"repaired": 1, "created": 1}}},
     "newDefect": {"candidate-01-patcha": 0, "candidate-02-patchb": 0, "candidate-03-combined": 1},
     "diffVerdicts": {"candidate-01-patcha": {"navownprobe": "DIFFERENTIAL-VERIFIED"},
                      "candidate-03-combined": {"navownprobe": "DIFFERENTIAL-NEUTRAL"}},
     "suppliedCombinationEspOnly": "candidate-03-combined"},
    verify=0,
    notes=["D7: a supplied-combination arm with a regression stays a regression whatever its repaired counts "
           "(no sum across probes, no net). The combined arm's own verdict is NEUTRAL and it is still a regression.",
           "D3: an ESP-only arm carries the limit sentence that its meshes, scripts and installer choices are not staged."]))

# ---------------------------------------------------------------- 08
cases.append(case(
    "case-08-interrupt-resume",
    "evalrun run is killed once a receipt exists, then resumed; then one fixture plugin's bytes change and resume runs "
    "again.",
    plan(), {},
    {"complete": False, "claim": "incomplete",
     "missingIncludes": NEVER_COMPLETE, "missingExcludes": [],
     "unknownApplicability": ["functional.linked-dependencies"],
     "journalHasInterrupted": True,
     "reusedAfterResumeNonEmpty": True,
     "invalidatedAfterPluginChange": ["geometry.navmesh-ownership", "geometry.object-overlap",
                                      "geometry.placement-contact", "loadability.masters-and-order"],
     "stillReusedAfterPluginChange": ["patch.author-advice"],
     "runIdsAtLeast": 2,
     "noCompletedWithoutReceiptFile": True},
    verify=None,
    notes=["D5: a running entry with no terminal line becomes interrupted and is re-queued, never counted.",
           "D6: a mismatch -> invalidated(<what changed>), dependents transitively; unchanged -> reused.",
           "Not asserted: whether resume re-runs an invalidated check in the same invocation (D6 does not say)."]))

# ---------------------------------------------------------------- 09
cases.append(case(
    "case-09-finalize-refusals",
    "A finished run directory copied three times and damaged once each: the registry file edited; one receipt deleted; "
    "one receipt's planId rewritten.",
    plan(), {},
    {"refusals": [
        {"damage": "registry-edited", "finalizeExit": 3, "cause": "registry-changed", "complete": False},
        {"damage": "receipt-deleted", "finalizeExit": 3, "cause": "receipt-missing", "complete": False},
        {"damage": "foreign-planid", "finalizeExit": 3, "cause": "foreign-run", "complete": False}]},
    verify=None,
    notes=["D7: refusals first (exit 3, complete false with the cause): registry-changed, plan-changed, "
           "receipt-missing, foreign-run."]))

# ---------------------------------------------------------------- 10
cases.append(case(
    "case-10-discovery-media",
    "adviceprobe returns a catalogued file whose hash is in no evaluated arm; media.json lists one item whose "
    "fingerprint matches an accepted baseline receipt and one whose fingerprint matches nothing.",
    plan(), {},
    {"complete": False, "claim": "incomplete",
     "missingIncludes": NEVER_COMPLETE, "missingExcludes": [],
     "unknownApplicability": ["functional.linked-dependencies"],
     "discovery": {"candidateQualifications": ["unqualified"], "sourcesNotSearchedPresent": True},
     "mediaEvidenceStrength": "supplied-media",
     "unassessedMediaIncludes": ["old-shot.png"],
     "unassessedMediaExcludes": ["matched-shot.png"]},
    verify=0,
    notes=["D7: qualification 'qualified' only for a plugin whose hash is in an evaluated arm, else 'unqualified'.",
           "D8: an item whose fingerprint equals an accepted receipt's fingerprint on that arm is evidence with "
           "strength supplied-media; anything else is unassessedMedia with the reason and is not evidence."]))

# ---------------------------------------------------------------- 11
cases.append(case(
    "case-11-bundle-relocate",
    "Case 01's finished run is bundled, verified in place, verified again after the bundle directory is copied "
    "elsewhere, then verified a third time after one evidence file is deleted from the copy.",
    plan(), {},
    {"verifySteps": [
        {"step": "in-place", "exit": 0, "bundleComplete": True},
        {"step": "relocated", "exit": 0, "bundleComplete": True},
        {"step": "file-deleted", "exit": 3, "bundleComplete": False, "listsMissingFile": True}],
     "excludedSaysBinaries": True,
     "noPluginBinariesInBundle": True},
    verify=0,
    notes=["D8: a relocated bundle validates identically; a missing or mismatched file -> exit 3 and "
           "bundle.complete false listing them; never a plugin or asset binary."]))

OUT.mkdir(parents=True, exist_ok=True)
for c in cases:
    (OUT / f"{c['case']}.json").write_text(json.dumps(c, indent=1) + "\n", encoding="utf-8")
print(f"wrote {len(cases)} case files to {OUT}")
