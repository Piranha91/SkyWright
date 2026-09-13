"""Build the evalrun fixtures: tiny plugins, a trimmed check registry, a scope.json and candidate files per
case, and the scenario each case's fake probes read. Deterministic — re-running it rewrites identical bytes,
so the plugin hashes (and therefore every configuration fingerprint) are stable across machines.

    python _geometry-harness/tests/fixtures/evalrun/make_fixtures.py --out DIR [--registry-from FILE]

Nothing it writes is tracked: every artifact here carries a path (the plugin files a receipt hashes, the
scope that names them, the scenarios that point at them) or is derived from the shipped registry, so
writing them into the checkout would dirty whatever tree ran the suite. `--out` defaults to the
gitignored `_generated/` beside this script; the suite passes its own temp directory.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]

PLUGINS = {
    "Skyrim.esm": b"fixture master: the game's own records; content is never parsed by a fake probe\n",
    "Update.esm": b"fixture master: the update master\n",
    "FixtureBase.esp": b"fixture base mod: places the records the target then re-cuts\n",
    "FixtureTarget.esp": b"fixture target: NAVM + REFR + CELL + QUST, interior and exterior placements\n",
    "PatchA.esp": b"fixture patch A: repairs R1 alone\n",
    "PatchB.esp": b"fixture patch B: repairs R2 alone\n",
}
BASE_ORDER = ["Skyrim.esm", "Update.esm", "FixtureBase.esp", "FixtureTarget.esp"]

# Q44 / case 16: a plugin that is NOT in `plugins/` and NOT in the scope — a supplied patch. Two mod
# folders provide the same filename with DIFFERENT bytes, so which copy a reader resolved is visible
# in the hash; a third copy (bytes C) is written into `overwrite/` by the test's own variant.
SUPPLIED_PLUGIN = "FixtureSupplied.esp"
SUPPLIED_BYTES = {
    "Supplied Patch A": b"fixture supplied patch A: the copy modlist.txt ranks first\n",
    "Supplied Patch B": b"fixture supplied patch B: a different copy of the same filename\n",
}
SUPPLIED_OVERWRITE_BYTES = b"fixture supplied patch C: the overwrite layer's copy\n"
# Mo2LoadOrder.ParseModlist's own shape: `#` a comment, `+` enabled, `-` disabled, a `…_separator`
# skipped from both lists, FILE ORDER with the TOP line the highest priority. `Fixture Enabled Mod`
# has no folder on disk, so layer 4 is exercised as "an absent folder is skipped"; the two disabled
# folders are layer 5, and A is above B, which is the only reason A wins.
MODLIST = ("# fixture modlist.txt: MO2 writes the highest-priority mod on the TOP line\n"
           "+Fixture Enabled Mod\n"
           "-Fixture patches_separator\n"
           "-Supplied Patch A\n"
           "-Supplied Patch B\n")

# The checks trimmed out of the real registry: one per family, plus the only real prerequisite chain
# (state.conditional-geometry <- state.switch-census) and one check of each non-runnable kind.
KEEP = ["loadability.masters-and-order", "loadability.dangling-references", "records.cell-attributes",
        "geometry.navmesh-ownership", "geometry.object-overlap", "geometry.placement-contact",
        "geometry.bos-overlay", "functional.linked-dependencies",
        "state.switch-census", "state.conditional-geometry",
        "functional.collective-route-connectivity", "environment.generated-output-regeneration",
        "patch.author-advice", "patch.order-evaluation", "runtime.supplied-observations"]

# Q43 / case 12 only: driftprobe's check declares its own population ("n/a (placement history; ...)") and
# state ("n/a"), so it is the second SUPPORTED probe whose accepted receipt on one arm carries a different
# fingerprint over the same plugins. It lives in its own trim so cases 01-11 are untouched.
KEEP_Q43 = ["functional.quest-alias-split"]

SCOPE = {
    "schemaVersion": 1, "probe": "scopeprobe", "generatedUtc": "2026-09-12T18:00:00Z",
    "complete": True, "walkFailures": [],
    "locationContributors": {
        "complete": True,
        "locations": [{"cell": "001234:FixtureBase.esp", "kind": "exterior", "name": "FixtureExterior",
                       "worldspace": "Tamriel", "x": 5, "y": 12,
                       "plugins": [{"name": "FixtureBase.esp", "recordKinds": ["CELL"]},
                                   {"name": "FixtureTarget.esp", "recordKinds": ["NAVM", "placed-record"]}]}],
        "limits": ["Local world-record contributors only."], "missing": []
    },
    "targets": [{
        "name": "FixtureTarget.esp", "provider": "fixture-mods",
        "sha256": None,
        "footprint": {
            "recordTypes": {"NAVM": {"new": 3, "override": 2, "className": "Navmesh"},
                            "REFR": {"new": 40, "override": 12, "className": "PlacedObject"},
                            "CELL": {"new": 0, "override": 4, "className": "Cell"},
                            "QUST": {"new": 1, "override": 0, "className": "Quest"}},
            "placed": {"total": 52, "interior": 30, "exterior": 22, "persistent": 2,
                       "navm": 5, "navmesh": 5, "land": 0,   # both spellings until the readers agree "perCell": {"FixtureInterior01": 30},
                       "exteriorCells": [["Tamriel", 5, 12]], "worldspaces": ["Tamriel"]},
            "expansion": {"neighbourCells": [["Tamriel", 4, 12], ["Tamriel", 6, 12]],
                          "doorDestinations": [{"door": "0A0007:FixtureTarget.esp",
                                                "destinationCell": "FixtureInterior01"}],
                          "unresolved": 0, "formLinksByMaster": {"Skyrim.esm": 61, "FixtureBase.esp": 8}},
            "masters": ["Skyrim.esm", "Update.esm", "FixtureBase.esp"],
            "scripts": {"recordsWithVmad": 2},
            "assets": {"count": 3, "paths": ["fixture/mesh01.nif", "fixture/mesh02.nif", "fixture/mesh03.nif"]},
            "nexusId": 999001,
        },
    }],
    # PLAN A-1: objects with a closed extent vocabulary; a check's applicability.dependsOnExtents reads these.
    # The sentences deliberately avoid the phrases the trigger-extent table matches on, so only a check that
    # DECLARES a dependency on one of these extents reads unknown from them.
    "unmeasured": [
        {"extent": "script-placement",
         "why": "a reference a Papyrus script moves or creates at runtime is in no static walk"},
        {"extent": "alias-fill",
         "why": "a quest alias fill is decided at runtime, so the object it points at is not known statically"},
        {"extent": "bos-swaps",
         "why": "the Base Object Swapper ini layer is not scanned; the ini list is not the set of swaps"},
        {"extent": "asset-replacers",
         "why": "a mesh or texture swapped by file precedence carries nothing to walk"},
        {"extent": "generated-output",
         "why": "whether DynDOLOD or an occlusion pass must be regenerated is not derivable here"},
    ],
    # `plugins` is filled in by main() with the fixture plugins' real paths and hashes: PLAN A-9 - a scope
    # with no plugin paths gives a resume nothing to re-hash, and D6's own rule then makes
    # refused-reuse(<gap>) the correct answer rather than invalidated. Both are fixtured (see SCOPE_NO_PLUGINS).
    "configuration": {"overlays": [], "plugins": [],
                      "providers": [{"Name": "overwrite", "Kind": "overwrite"},
                                    {"Name": "fixture-mods", "Kind": "mod"},
                                    {"Name": "Data", "Kind": "data"}],
                      "population": "load-time", "state": "baseline",
                      "note": "the fixture configuration reads no BOS ini files"},
}


def scope_without_plugins(scope):
    """The same scope with the configuration's plugin list emptied: nothing for a resume to re-hash, so
    D6's `refused-reuse(<gap>)` is the correct answer and `invalidated` would be a false certainty."""
    import copy
    s = copy.deepcopy(scope)
    s["configuration"]["plugins"] = []
    s["configuration"]["note"] = "this scope names no plugin paths: a consumer cannot state what it read"
    return s


def candidate(name, plugins, changes, kind_note=None, mods=None, provenance=None) -> dict:
    d = {"SchemaVersion": 1, "Name": name, "BaseProfile": "fixture", "Plugins": plugins,
         "Changes": changes, "Provenance": provenance or {"fixture": "evalrun test fixture"}}
    if mods is not None:
        d["Mods"] = mods
    if kind_note:
        d["Provenance"]["note"] = kind_note
    return d


def write_json(p: Path, doc):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")


ROWS = {
    "navownprobe": [{"kind": "CLOBBERED", "a": "FixtureTarget.esp", "b": "FixtureBase.esp",
                     "summary": "3 navmesh record(s) re-cut without a declared dependency",
                     "formKey": "0A0001:FixtureBase.esp", "role": "navmesh", "label": "FixtureInterior01",
                     "physical": "Holds", "detail": "the loser's mesh reaches 260u past the winner's"}],
    "overlapprobe": [{"kind": "OVERLAP", "a": "FixtureTarget.esp", "b": "FixtureBase.esp",
                      "summary": "two placed references intersect", "formKey": "0A0002:FixtureBase.esp",
                      "role": "reference", "label": "FixtureInterior01", "physical": "Holds",
                      "detail": "the boxes intersect by 44u"}],
    "contactprobe": [{"kind": "CONTACT", "a": "FixtureTarget.esp", "b": "FixtureBase.esp",
                      "summary": "a dependent reference touches nothing", "formKey": "0A0003:FixtureBase.esp",
                      "role": "reference", "label": "FixtureExterior", "physical": "Holds",
                      "detail": "nearest support 91u below"}],
}
R3 = {"kind": "CLOBBERED", "a": "PatchA.esp", "b": "PatchB.esp",
      "summary": "the combination re-cuts a navmesh neither patch touches alone",
      "formKey": "0A0009:FixtureBase.esp", "role": "navmesh", "label": "FixtureInterior01",
      "physical": "Holds", "detail": "the loser's mesh reaches 310u past the winner's"}


def ok(rows=None, judged=42, **kw):
    e = {"behaviour": "ok", "rows": rows if rows is not None else [], "judged": judged}
    e.update(kw)
    return e


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--registry-from")
    ap.add_argument("--out", default=str(HERE / "_generated"),
                    help="where to write the generated fixtures (never the tracked tree)")
    a = ap.parse_args()
    out_root = Path(a.out)
    out_root.mkdir(parents=True, exist_ok=True)

    # ---------------------------------------------------------------- plugins
    pdir = out_root / "plugins"
    pdir.mkdir(parents=True, exist_ok=True)
    for name, body in PLUGINS.items():
        (pdir / name).write_bytes(body)

    # ---------------------------------------------------------------- the MO2-shaped instance (Q44)
    # An instance tree beside `plugins/`, for the ONE case that resolves a plugin the scope does not
    # list. Every layer of D1's walk is present and empty or absent except the one that must win.
    inst = out_root / "instance"
    (inst / "overwrite").mkdir(parents=True, exist_ok=True)
    for folder, body in SUPPLIED_BYTES.items():
        (inst / "mods" / folder).mkdir(parents=True, exist_ok=True)
        (inst / "mods" / folder / SUPPLIED_PLUGIN).write_bytes(body)
    (inst / "profiles" / "fixture").mkdir(parents=True, exist_ok=True)
    (inst / "profiles" / "fixture" / "modlist.txt").write_text(MODLIST, encoding="utf-8", newline="\n")
    gdir = out_root / "game"
    (gdir / "Data").mkdir(parents=True, exist_ok=True)

    # The configuration the scope reports, with the fixture plugins' real paths and content hashes - what a
    # resume re-hashes (PLAN D6 / A-9). Written here rather than typed above because the hashes are of files
    # this run just wrote.
    SCOPE["configuration"]["plugins"] = [
        {"Name": name, "Path": str(pdir / name).replace("\\", "/"),
         "Sha256": hashlib.sha256((pdir / name).read_bytes()).hexdigest(),
         "Bytes": (pdir / name).stat().st_size, "Provider": "fixture-mods"}
        for name in BASE_ORDER
    ]

    sys.path.insert(0, str(HERE))
    from fakeprobe import fingerprint                                              # the canonical text, once
    SCOPE["configuration"]["fingerprint"] = fingerprint(
        SCOPE["configuration"]["plugins"], SCOPE["configuration"]["providers"],
        SCOPE["configuration"]["overlays"], "load-time", "baseline")
    SCOPE["configuration"]["source"] = "candidate:candidate-00-baseline.json"

    # ---------------------------------------------------------------- registry (a trim of the real one)
    if a.registry_from:
        real = json.loads(Path(a.registry_from).read_text(encoding="utf-8"))
    else:
        src = REPO / ".claude/skills/world-edit-compatibility/references/check-registry.json"
        if src.exists():
            real = json.loads(src.read_text(encoding="utf-8"))
        else:
            real = json.loads(subprocess.run(
                ["git", "show", "q42-agent-a:.claude/skills/world-edit-compatibility/references/check-registry.json"],
                cwd=REPO, capture_output=True, text=True, check=True).stdout)
    def trim(ids, name, extra_about=""):
        keep = [c for c in real["checks"] if c["id"] in ids]
        absent = [k for k in ids if k not in {c["id"] for c in keep}]
        if absent:
            raise SystemExit(f"the real registry no longer carries {absent}; the fixture trim must be revisited")
        fams = {c["family"] for c in keep}
        trimmed = dict(real)
        trimmed["checks"] = keep
        trimmed["families"] = [f for f in real["families"] if f["id"] in fams]
        trimmed["about"] = ("A TRIM of the shipped check registry, for the evalrun fixtures: every check object is "
                            "verbatim, only the set is smaller. " + extra_about + real.get("about", ""))
        write_json(out_root / "registry" / name, trimmed)
        return keep

    keep = trim(KEEP, "fixture-check-registry.json")
    # Case 12 needs a SECOND supported probe that declares its own population and state, so two accepted
    # receipts of one arm legitimately carry different fingerprints over identical plugins (D2(g)/(h)).
    # It gets its own trim file: cases 01-11 keep theirs unchanged, and their planIds do not move.
    trim(KEEP + KEEP_Q43, "fixture-check-registry-q43.json",
         "This trim adds " + ", ".join(KEEP_Q43) + " for the identity-binding case. ")

    # ---------------------------------------------------------------- cases
    cases = out_root / "cases"
    if cases.exists():
        shutil.rmtree(cases)

    def case_dir(name, cands, scenario, scope=None, extra_scenarios=None):
        d = cases / name
        write_json(d / "scope.json", scope or SCOPE)
        for fname, doc in cands.items():
            write_json(d / "candidates" / fname, doc)
        write_json(d / "candidates" / "candidates.json", {
            "SchemaVersion": 1,
            "Candidates": [{"File": f, "ViolatesAdvice": [], "SatisfiesAdvice": []} for f in cands],
            "Budget": {"evaluated": len(cands) - 1, "legalNotEvaluated": 0,
                       "note": "fixture manifest: the search was not run"},
            "Contradictions": []})
        scenario.setdefault("pluginDir", str(pdir).replace("\\", "/"))
        scenario.setdefault("scopeFrom", str(d / "scope.json").replace("\\", "/"))
        write_json(d / "scenario.json", scenario)
        for fname, damage in (extra_scenarios or {}).items():
            import copy as _copy
            alt = _copy.deepcopy(scenario)
            alt["identityDamage"] = damage
            write_json(d / fname, alt)

    base = candidate("candidate-00-baseline", BASE_ORDER, ["the profile's own order"])
    reorder = candidate("candidate-01-reorder",
                        ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"],
                        ["moved FixtureTarget.esp above FixtureBase.esp"])
    reorder_b = candidate("candidate-02-reorder-b",
                          ["Update.esm", "Skyrim.esm", "FixtureBase.esp", "FixtureTarget.esp"],
                          ["moved Update.esm above Skyrim.esm"])

    def arms_ok(stems, rows_by_probe=ROWS):
        out = {}
        for probe, rows in rows_by_probe.items():
            out[probe] = {s: ok(rows) for s in stems}
        for probe in ("bosprobe", "switchprobe", "stateprobe", "scopeprobe", "adviceprobe", "patchprobe", "driftprobe"):
            out.setdefault(probe, {"*": ok()})
        return out

    stems = ["baseline", "candidate-01-reorder", "candidate-01-reorder.reverse"]

    # 01 / 02 / 08 / 11 share one shape: everything ok, the candidate arm repeats the baseline's rows.
    for name in ("case-01-geometry-and-quest", "case-02-select-three-probes",
                 "case-08-interrupt-resume", "case-11-bundle-relocate"):
        case_dir(name, {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder},
                 {"arms": arms_ok(stems)})
    write_json(cases / "case-08-interrupt-resume" / "scope-no-plugins.json", scope_without_plugins(SCOPE))

    # 03 — a valid null check beside two failed extractions.
    arms = {"navownprobe": {s: ok([], judged=42) for s in stems},
            "overlapprobe": {s: {"behaviour": "extraction-failed", "unjudgeable": 17} for s in stems},
            "contactprobe": {s: ok([], judged=0) for s in stems}}
    arms["contactprobe"] = {s: {"behaviour": "ok", "rows": [], "judged": 0,
                                "coverage": [{"Population": "contact pairs", "Judged": 0, "Unjudgeable": {}}]}
                            for s in stems}
    for probe in ("bosprobe", "switchprobe", "stateprobe", "scopeprobe", "adviceprobe", "patchprobe", "driftprobe"):
        arms[probe] = {"*": ok()}
    case_dir("case-03-null-and-extraction",
             {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder}, {"arms": arms})

    # 04 — switchprobe fails, so state.conditional-geometry loses its prerequisite; one candidate is held back.
    arms = arms_ok(stems)
    arms["switchprobe"] = {"*": {"behaviour": "exit", "code": 4}}
    case_dir("case-04-blocked-and-budget",
             {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder,
              "candidate-02-reorder-b.json": reorder_b}, {"arms": arms})

    # 05 — six rejection causes, one per (probe, arm).
    stale = candidate("candidate-01-stale", reorder["Plugins"], ["moved FixtureTarget.esp above FixtureBase.esp"])
    broken = candidate("candidate-02-broken", reorder_b["Plugins"], ["moved Update.esm above Skyrim.esm"])
    arms = {p: {"baseline": ok(ROWS[p])} for p in ROWS}
    arms["navownprobe"]["candidate-01-stale"] = {"behaviour": "stale-output", "rows": ROWS["navownprobe"]}
    arms["overlapprobe"]["candidate-01-stale"] = {"behaviour": "wrong-state", "rows": ROWS["overlapprobe"]}
    arms["contactprobe"]["candidate-01-stale"] = {"behaviour": "illegal"}
    arms["navownprobe"]["candidate-02-broken"] = {"behaviour": "no-emit"}
    arms["overlapprobe"]["candidate-02-broken"] = {"behaviour": "wrong-probe-name", "probeName": "driftprobe",
                                                   "rows": ROWS["overlapprobe"]}
    arms["contactprobe"]["candidate-02-broken"] = {"behaviour": "no-configuration", "rows": ROWS["contactprobe"]}
    for p in ROWS:
        arms[p]["*"] = ok(ROWS[p])
    for probe in ("bosprobe", "switchprobe", "stateprobe", "scopeprobe", "adviceprobe", "patchprobe", "driftprobe"):
        arms[probe] = {"*": ok()}
    case_dir("case-05-rejections",
             {"candidate-00-baseline.json": base, "candidate-01-stale.json": stale,
              "candidate-02-broken.json": broken}, {"arms": arms})

    # 06 — the row leaves the class and its predicate still holds: RECLASSIFIED, never repaired.
    sham = candidate("candidate-01-sham", BASE_ORDER + ["PatchA.esp"], ["enabled PatchA.esp (the sham)"])
    arms = {"navownprobe": {"baseline": ok(ROWS["navownprobe"]),
                            "candidate-01-sham": {"behaviour": "ok", "rows": [], "judged": 42,
                                                  "predicateDefault": "Holds"},
                            "candidate-01-sham.reverse": ok(ROWS["navownprobe"])},
            "overlapprobe": {"*": ok()}, "contactprobe": {"*": ok()}}
    for probe in ("bosprobe", "switchprobe", "stateprobe", "scopeprobe", "adviceprobe", "patchprobe", "driftprobe"):
        arms[probe] = {"*": ok()}
    case_dir("case-06-attribution",
             {"candidate-00-baseline.json": base, "candidate-01-sham.json": sham}, {"arms": arms})

    # 07 — two patches that pass alone and regress together.
    pa = candidate("candidate-01-patcha", BASE_ORDER + ["PatchA.esp"], ["enabled PatchA.esp"])
    pb = candidate("candidate-02-patchb", BASE_ORDER + ["PatchB.esp"], ["enabled PatchB.esp"])
    comb = candidate("candidate-03-combined", BASE_ORDER + ["PatchA.esp", "PatchB.esp"],
                     ["enabled PatchA.esp and PatchB.esp together"],
                     kind_note="supplied combination, ESP-only: the patches' meshes, scripts and installer "
                               "choices are NOT staged, so this arm is a result for the records only")
    nav, ovl = ROWS["navownprobe"], ROWS["overlapprobe"]
    arms = {
        "navownprobe": {
            "baseline": ok(nav),
            "candidate-01-patcha": {"behaviour": "ok", "rows": [], "judged": 42, "predicateDefault": "Gone"},
            "candidate-01-patcha.reverse": ok(nav),
            "candidate-02-patchb": ok(nav),
            "candidate-02-patchb.reverse": ok(nav),
            "candidate-03-combined": {"behaviour": "ok", "rows": [R3], "judged": 42, "predicateDefault": "Gone"},
            # the reverse arm measures the COMBINED emit's rows under the baseline order: R3 is not there -> Gone
            "candidate-03-combined.reverse": {"behaviour": "ok", "rows": nav,
                                              "predicates": {R3["formKey"]: "Gone"}, "predicateDefault": "Gone"},
        },
        "overlapprobe": {
            "baseline": ok(ovl),
            "candidate-01-patcha": ok(ovl),
            "candidate-01-patcha.reverse": ok(ovl),
            "candidate-02-patchb": {"behaviour": "ok", "rows": [], "judged": 42, "predicateDefault": "Gone"},
            "candidate-02-patchb.reverse": ok(ovl),
            "candidate-03-combined": {"behaviour": "ok", "rows": [], "judged": 42, "predicateDefault": "Gone"},
            "candidate-03-combined.reverse": ok(ovl),
        },
        "contactprobe": {"*": ok(ROWS["contactprobe"])},
    }
    for probe in ("bosprobe", "switchprobe", "stateprobe", "scopeprobe", "adviceprobe", "patchprobe", "driftprobe"):
        arms[probe] = {"*": ok()}
    case_dir("case-07-combined-regression",
             {"candidate-00-baseline.json": base, "candidate-01-patcha.json": pa,
              "candidate-02-patchb.json": pb, "candidate-03-combined.json": comb}, {"arms": arms})

    # 09 / 10 reuse case 01's shape; 10 adds the discovery hit and the media list.
    case_dir("case-09-finalize-refusals",
             {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder}, {"arms": arms_ok(stems)})
    arms = arms_ok(stems)
    arms["adviceprobe"] = {"*": {"behaviour": "ok", "modId": "999001",
                                 "warnings": ["the stored page copy is older than the catalogue snapshot"]}}
    arms["patchprobe"] = {"*": {"behaviour": "ok",
                                "json": {"probe": "patchprobe", "plugins": 4,
                                         "rows": [{"name": "CommunityPatch.esp", "sha256": "f00d" + "0" * 60,
                                                   "installed": False}]}}}
    case_dir("case-10-discovery-media",
             {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder}, {"arms": arms})

    # ---------------------------------------------------------------- Q43: cases 12-15
    # 12 — identity binding. Three arms: the baseline, a legal reorder, and one whose order carries a
    # plugin the scope does not list (so D1 makes its requested hash `unstated`). driftprobe runs with its
    # registry-declared population and state, which is what makes it the differently-populated control.
    unstated = candidate("candidate-02-unstated", BASE_ORDER + ["PatchA.esp"],
                         ["enabled PatchA.esp, which this scope's configuration does not list"])
    stems12 = ["baseline", "candidate-01-reorder", "candidate-01-reorder.reverse",
               "candidate-02-unstated", "candidate-02-unstated.reverse"]
    arms12 = arms_ok(stems12)
    arms12["driftprobe"] = {"*": {"behaviour": "ok", "rows": [], "judged": 7,
                                  "population": "n/a (placement history; no enable-state filter)",
                                  "state": "n/a"}}
    cand_order = ["Skyrim.esm", "Update.esm", "FixtureTarget.esp", "FixtureBase.esp"]
    case_dir("case-12-identity-binding",
             {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder,
              "candidate-02-unstated.json": unstated},
             {"arms": arms12},
             extra_scenarios={
                 # keyed on the ORDER stem: every emit-writing probe of that arm is damaged the same way,
                 # so the arm is internally consistent and only the request rule can reject it.
                 "scenario-wrong-order.json": [
                     {"orderStem": "candidate-01-reorder", "kind": "wrong-order"}],
                 "scenario-wrong-hash.json": [
                     {"orderStem": "candidate-01-reorder", "kind": "wrong-hash",
                      "plugin": "FixtureTarget.esp"}],
                 "scenario-bad-fingerprint.json": [
                     {"orderStem": "candidate-01-reorder", "kind": "bad-fingerprint",
                      "fingerprint": "f" * 64}],
                 "scenario-empty-identity.json": [
                     {"orderStem": "candidate-01-reorder", "kind": "empty-identity"}],
                 # keyed on the EMIT stem: only the reverse part, whose reference is the BASELINE's request
                 "scenario-reverse-wrong-reference.json": [
                     {"emitStem": "candidate-01-reorder.reverse", "kind": "wrong-reference-order",
                      "plugins": cand_order}],
             })

    # 13 — the reuse boundary. Case 01's shape; the damage is done to the run and to this case's own
    # scope/candidate copies, never to a fixture another case reads.
    case_dir("case-13-reuse-boundary",
             {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder},
             {"arms": arms_ok(stems)})

    # 14 — the evidence chain. Zero rows with judged > 0 on every geometry probe, so the checks are
    # COMPLETED NULL CHECKS: `completedNullChecks` is the list the deleted evidence has to fall out of.
    arms14 = {p: {s: ok([], judged=42) for s in stems} for p in ROWS}
    for probe in ("bosprobe", "switchprobe", "stateprobe", "scopeprobe", "adviceprobe", "patchprobe",
                  "driftprobe"):
        arms14[probe] = {"*": ok()}
    case_dir("case-14-evidence-chain",
             {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder},
             {"arms": arms14})

    # 15 — portability. Case 01's shape; the case generates its OWN fixture root and deletes it before
    # verifying, which is why this must be a separate case directory rather than a shared one.
    case_dir("case-15-portable-incomplete",
             {"candidate-00-baseline.json": base, "candidate-01-reorder.json": reorder},
             {"arms": arms_ok(stems)})

    # ---------------------------------------------------------------- Q44: case 16
    # 16 — a supplied plugin's identity at plan time. The candidates dir holds the BASELINE ONLY: the
    # supplied arm comes from `--supplied FixtureSupplied.esp=<the file under supplied/>`, whose
    # candidate file lives OUTSIDE candidates/ exactly as the lab's does (a file inside it would make
    # a second, ordinary candidate arm out of the same order).
    supplied_cand = candidate("candidate-02-supplied", BASE_ORDER + [SUPPLIED_PLUGIN],
                              [f"enabled {SUPPLIED_PLUGIN}, a supplied patch the profile does not load"],
                              kind_note="supplied combination, ESP-only")
    stems16 = ["baseline", "candidate-02-supplied", "candidate-02-supplied.reverse"]
    case_dir("case-16-supplied-identity", {"candidate-00-baseline.json": base},
             {"arms": arms_ok(stems16)},
             extra_scenarios={
                 # The probe reads the LOSING copy of the two folders that provide the name: the
                 # emit's Configuration is canonical in every other respect and its fingerprint is
                 # the digest of what it states, so only the hash rule can fire.
                 "scenario-wrong-copy.json": [
                     {"orderStem": "candidate-02-supplied", "kind": "wrong-copy",
                      "plugin": SUPPLIED_PLUGIN, "folder": "Supplied Patch B"}],
                 # The same damage over a folder whose bytes are IDENTICAL to the winner's — D2's
                 # accepted-with-a-limit direction. The folder is planted by the test on its own
                 # copy of the instance tree, which is why only the scenario lives here.
                 "scenario-identical-copy.json": [
                     {"orderStem": "candidate-02-supplied", "kind": "wrong-copy",
                      "plugin": SUPPLIED_PLUGIN, "folder": "Supplied Patch A copy"}],
             })
    write_json(cases / "case-16-supplied-identity" / "supplied" / "candidate-02-supplied.json",
               supplied_cand)
    # The other half of the `Mods` pair: the SAME order with an EMPTY Mods list. `cand.Mods ??
    # comp.EnabledMods` falls back only on an absent one, so this candidate stages no mod folders
    # and cannot see a copy inside the view's own folder.
    write_json(cases / "case-16-supplied-identity" / "supplied" / "candidate-03-supplied-empty-mods.json",
               candidate("candidate-03-supplied-empty-mods", BASE_ORDER + [SUPPLIED_PLUGIN],
                         [f"enabled {SUPPLIED_PLUGIN} with an empty Mods list: no mod folder is staged"],
                         mods=[], kind_note="supplied combination, ESP-only"))

    print(f"fixtures written under {out_root}")
    print(f"  plugins: {len(PLUGINS)}   registry checks: {len(keep)}   cases: {len(list(cases.iterdir()))}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
