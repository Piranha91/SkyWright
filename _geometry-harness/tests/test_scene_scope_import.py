"""Known-answer tests for overlapprobe's --scene-scope-from bound contract.

These drive the real overlapprobe binary, so they exercise SceneCapture.ReadScope and the scope
merge themselves rather than a re-implementation. Scope reading happens after the candidate asset
preflight, so the tests need a real MO2 target and a real candidate file; they skip, loudly, when
those are absent.

Point them at a target with:
    HOUSECARL_MO2_INSTANCE, HOUSECARL_MO2_PROFILE, HOUSECARL_GAME_PATH  (as any probe needs)
    HOUSECARL_TEST_CANDIDATE  -> an explicit-Mods candidate .json that passes --scene-preflight

Optionally, to check imported-identity accounting against a real captured pair:
    HOUSECARL_TEST_SCOPE_MANIFEST     -> a manifest used as --scene-scope-from
    HOUSECARL_TEST_CAPTURED_MANIFEST  -> a manifest captured while importing that scope

The contract under test: a reusable scope must declare at least one explicit bound. overlapprobe
emits exactly two kinds -- scene.discovery.seedReferences[] and scene.discovery.cells[] -- and
either is sufficient. An imported declared cell then joins the capture's own --scene-cell set and
is subject to the same bounds checks a typed --scene-cell gets.

Every case asserts the EXACT expected outcome, process exit code included. Asserting only that
some error string is absent would let an unrelated failure -- a crash, a bad path, a missing
target -- pass as success, which is not a test.

Fixtures are generic on purpose: no real cell, plugin or reference from any particular load order
appears, so the contract cannot be fitted to one case.
"""
import json
import os
import subprocess
import sys

try:
    import pytest
except ImportError:  # the file still runs standalone; see the __main__ block below
    pytest = None

HARNESS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
# HOUSECARL_TEST_OVERLAPPROBE runs the cases against another build (a baseline or a sabotage arm).
EXE = os.environ.get("HOUSECARL_TEST_OVERLAPPROBE") or str(buildlayout.program("overlapprobe"))
CANDIDATE = os.environ.get("HOUSECARL_TEST_CANDIDATE")

SKIP_REASON = ("needs a built overlapprobe and HOUSECARL_TEST_CANDIDATE pointing at a real "
               "candidate that passes --scene-preflight")
UNAVAILABLE = not os.path.exists(EXE) or not CANDIDATE or not os.path.exists(CANDIDATE or "")
if pytest is not None:
    pytestmark = pytest.mark.skipif(UNAVAILABLE, reason=SKIP_REASON)

# Generic identities. None of these name a real cell, plugin or reference. The cell keys are
# ordered deliberately: overlapprobe walks its declared cells in ordinal FormKey order, so the
# lowest unusable cell is the one it refuses on, whichever scope contributed it.
CELL_LOW = "000AAA:TestScope.esm"
CELL_MID = "000BBB:TestScope.esm"
CELL_HIGH = "000CCC:TestScope.esm"
REF_A = "000B01:TestScope.esm"
REF_B = "000B02:TestScope.esm"
SPACE = "00003C:TestScope.esm"
PAIR = REF_A + "||" + REF_B if REF_A <= REF_B else REF_B + "||" + REF_A

NO_BOUND_CARRIED = "scene scope resolved no seed references"


def unusable_cell_refusal(scope_path, cell):
    """A cell whose owner IS loaded but which holds no placement gives no bounded footprint, so once the
    bound reaches scoping the capture refuses on it with this text, naming the cell and the scope file
    that declared it. (A cell whose owner the order neither loads nor masters is skipped instead; see
    absent_cell_skip.)"""
    return (f"--scene-scope-from '{os.path.abspath(scope_path)}' declares cell {cell}, "
            "which has no placed reference from which to derive a bounded exterior footprint")


OWNER = "TestScope.esm"   # loaded by no real order and mastered by none: its cells cannot exist there
LOADED_UNKNOWN_CELL = "FFFFF0:Skyrim.esm"   # an owner every order loads, an id no cell uses


def absent_cell_skip(scope_path, cell):
    """An imported cell whose owning plugin the setup neither loads nor masters does not exist in that
    setup, so it bounds nothing there. The skip line is the proof the bound arrived: it names the cell,
    its owner and the scope file that declared it."""
    return (f"--scene-scope-from '{os.path.abspath(scope_path)}' declares cell {cell}, owned by {OWNER}, "
            "which this setup neither loads nor masters: the cell does not exist here and bounds nothing")


def _absent_declared(tmp_path):
    with open(tmp_path / "out.json", encoding="utf-8") as f:
        return json.load(f)["scene"]["discovery"]["absentDeclaredCells"]


def _record(formid, cell=CELL_LOW, interior=True):
    return {
        "formid": formid,
        "fields": {
            "cell": {"formid": cell, "editorid": "TestCell", "interior": interior},
            "space": SPACE,
            "position": [1.0, 2.0, 3.0],
        },
        "geometry": {"worldBounds": {"min": [0.0, 0.0, 0.0], "max": [1.0, 1.0, 1.0]}},
    }


def _manifest(*, seeds, cells, records=None, pairs=None, cells_kind="list",
              seed_definitions=()):
    # A producer emits seedDefinitions only once it has seeds to define; when the member is
    # absent ReadScope derives the definitions from records[] instead. Both shapes are real.
    discovery = {"seedReferences": list(seeds)}
    if seed_definitions is not None:
        discovery["seedDefinitions"] = list(seed_definitions)
    if cells_kind == "list":
        discovery["cells"] = list(cells)
    elif cells_kind == "not-an-array":
        discovery["cells"] = CELL_LOW  # a bare string where an array is required
    elif cells_kind == "absent":
        pass
    return {
        "scene": {"id": "fixture", "role": "combined", "discovery": discovery},
        "records": records if records is not None else [_record(REF_A), _record(REF_B)],
        "pairs": pairs if pairs is not None else [{"pairKey": PAIR, "a": REF_A, "b": REF_B}],
    }


def _write(tmp_path, name, payload):
    path = tmp_path / name
    path.write_text(json.dumps(payload), encoding="utf-8")
    return str(path)


def _run(tmp_path, *scope_paths, extra=()):
    out = tmp_path / "out.json"
    argv = [EXE, "--scene-id", "contract", "--scene-role", "combined", "--order", CANDIDATE,
            "--geometry-manifest", str(out)]
    for scope_path in scope_paths:
        argv += ["--scene-scope-from", scope_path]
    argv += list(extra)
    proc = subprocess.run(argv, capture_output=True, text=True, timeout=1800)
    return proc.returncode, (proc.stderr or "") + (proc.stdout or "")


def _scope_refusal(text):
    """The message overlapprobe prints when ReadScope itself rejected the file."""
    for line in text.splitlines():
        if line.startswith("could not read --scene-scope-from"):
            return line
    return None


def _tail(text, n=4):
    return "\n".join(text.strip().splitlines()[-n:])


def _assert_exact(code, text, *, expect_code, expect_contains):
    """Pins the process outcome as well as the message, so an unrelated failure cannot be
    mistaken for the expected one."""
    assert code == expect_code, \
        f"expected exit {expect_code}, got {code}. Last output:\n{_tail(text)}"
    assert expect_contains in text, \
        f"expected output to contain:\n  {expect_contains}\nLast output:\n{_tail(text)}"


# --- the bound contract: a declared cell is a bound -----------------------------------------

def test_cell_only_scope_is_accepted_and_its_bound_is_carried(tmp_path):
    """C608. A capture declared by --scene-cell carries cells[] and an empty seedReferences[]. Refusing
    it was the defect. The imported cell's owner is in no plugin list of the evaluated order, so the
    cell does not exist there (P111): the capture completes, prints the skip line that can ONLY be
    reached once the imported cell has joined the capture's own cell set, and names the cell in
    absentDeclaredCells with the scope that declared it."""
    scope = _write(tmp_path, "cell-only.json", _manifest(seeds=[], cells=[CELL_LOW]))
    code, text = _run(tmp_path, scope)
    assert _scope_refusal(text) is None, f"the scope must not be refused: {_scope_refusal(text)}"
    _assert_exact(code, text, expect_code=0, expect_contains=absent_cell_skip(scope, CELL_LOW))
    assert _absent_declared(tmp_path) == [{"cell": CELL_LOW, "owningPlugin": OWNER,
        "declaredBy": f"--scene-scope-from '{os.path.abspath(scope)}'", "basis": "owning-plugin-not-loaded"}]


def test_seed_referenced_scope_still_captures(tmp_path):
    """Regression: generalising the bound must not disturb the original seed-referenced import,
    which scopes a complete capture. Pinned to exit 0 -- the capture has to actually finish."""
    scope = _write(tmp_path, "seeded.json",
                   _manifest(seeds=[REF_A], cells=[], seed_definitions=None))
    code, text = _run(tmp_path, scope)
    assert _scope_refusal(text) is None, f"the scope must not be refused: {_scope_refusal(text)}"
    assert NO_BOUND_CARRIED not in text, "the imported seed bound was parsed but not carried"
    assert code == 0, \
        f"a seed-bounded import must still complete a capture; exit {code}\n{_tail(text)}"


def test_cell_bounded_scope_with_empty_population_is_accepted(tmp_path):
    """A declared cell that happens to enumerate nothing is still a bound. Refusing it would make
    'this cell was measured and held nothing' indistinguishable from 'never measured' -- the same
    absence-from-emptiness inference the consumer's absence rule forbids."""
    scope = _write(tmp_path, "empty-but-bounded.json",
                   _manifest(seeds=[], cells=[CELL_LOW], records=[], pairs=[]))
    code, text = _run(tmp_path, scope)
    assert _scope_refusal(text) is None, f"the scope must not be refused: {_scope_refusal(text)}"
    assert NO_BOUND_CARRIED not in text, "an absent declared cell is still a bound"
    _assert_exact(code, text, expect_code=0, expect_contains=absent_cell_skip(scope, CELL_LOW))


# --- two scopes: both contributions must independently reach the bound ----------------------
#
# Both scopes' cells are owned by a plugin the order does not load, so each is skipped with its own
# line. If either scope's cells were dropped from the merge, that scope's line would be missing.

def test_two_scopes_first_contribution_reaches_the_bound(tmp_path):
    """C609."""
    a = _write(tmp_path, "scope-a.json", _manifest(seeds=[], cells=[CELL_LOW]))
    b = _write(tmp_path, "scope-b.json", _manifest(seeds=[], cells=[CELL_MID]))
    code, text = _run(tmp_path, a, b)
    _assert_exact(code, text, expect_code=0, expect_contains=absent_cell_skip(a, CELL_LOW))
    assert absent_cell_skip(b, CELL_MID) in text, "the second scope's cell never reached the bound"
    assert [r["cell"] for r in _absent_declared(tmp_path)] == [CELL_LOW, CELL_MID]


def test_two_scopes_second_contribution_reaches_the_bound(tmp_path):
    """C610. Same shape with the scopes' cells swapped in order, so each scope's line is checked
    against its own path whichever owns the lower cell."""
    a = _write(tmp_path, "scope-a.json", _manifest(seeds=[], cells=[CELL_HIGH]))
    b = _write(tmp_path, "scope-b.json", _manifest(seeds=[], cells=[CELL_MID]))
    code, text = _run(tmp_path, a, b)
    _assert_exact(code, text, expect_code=0, expect_contains=absent_cell_skip(b, CELL_MID))
    assert absent_cell_skip(a, CELL_HIGH) in text, "the first scope's cell never reached the bound"
    declared_by = {r["cell"]: r["declaredBy"] for r in _absent_declared(tmp_path)}
    assert declared_by == {CELL_MID: f"--scene-scope-from '{os.path.abspath(b)}'",
                           CELL_HIGH: f"--scene-scope-from '{os.path.abspath(a)}'"}


# --- a cell that can exist keeps the guard ---------------------------------------------------

def test_loaded_owner_cell_without_placement_is_still_refused(tmp_path):
    """C611. The owner is loaded, so the cell could exist; with no placed reference it gives no
    bounded footprint, and the capture refuses as before. Deleting the guard turns this green-to-red."""
    scope = _write(tmp_path, "loaded-owner.json", _manifest(seeds=[], cells=[LOADED_UNKNOWN_CELL]))
    code, text = _run(tmp_path, scope)
    _assert_exact(code, text, expect_code=2,
                  expect_contains=unusable_cell_refusal(scope, LOADED_UNKNOWN_CELL))


def test_typed_scene_cell_with_absent_owner_is_refused(tmp_path):
    """C612. A typed --scene-cell is the operator's own bound; naming a plugin the order does not load
    is refused with the owner named, never skipped."""
    code, text = _run(tmp_path, extra=("--scene-cell", CELL_LOW))
    _assert_exact(code, text, expect_code=2,
                  expect_contains=f"--scene-cell declares cell {CELL_LOW}, owned by {OWNER}, which this setup "
                                  "neither loads nor masters")


# --- an undeclared or malformed scope is still refused --------------------------------------

def test_scope_with_no_declared_bound_is_refused(tmp_path):
    """An unbounded import is what the guard exists to stop; deleting the guard would pass every
    accept case above while breaking this one."""
    scope = _write(tmp_path, "unbounded.json", _manifest(seeds=[], cells=[]))
    code, text = _run(tmp_path, scope)
    assert code == 2, f"expected exit 2, got {code}\n{_tail(text)}"
    refusal = _scope_refusal(text)
    assert refusal is not None and "declares no bound" in refusal, refusal
    assert "seedReferences" in refusal and "cells" in refusal, \
        "the refusal must name both bound kinds so the operator knows what would fix it"


def test_scope_with_absent_cells_receipt_and_no_seeds_is_refused(tmp_path):
    """An older manifest with no cells[] member at all still has to declare a seed bound."""
    scope = _write(tmp_path, "no-cells-key.json",
                   _manifest(seeds=[], cells=[], cells_kind="absent"))
    code, text = _run(tmp_path, scope)
    assert code == 2, f"expected exit 2, got {code}\n{_tail(text)}"
    assert "declares no bound" in (_scope_refusal(text) or "")


def test_malformed_cells_receipt_is_refused(tmp_path):
    """cells[] must be an array, not a bare string."""
    scope = _write(tmp_path, "cells-not-array.json",
                   _manifest(seeds=[], cells=[], cells_kind="not-an-array"))
    code, text = _run(tmp_path, scope)
    assert code == 2, f"expected exit 2, got {code}\n{_tail(text)}"
    assert "cells must be an array" in (_scope_refusal(text) or "")


def test_malformed_cell_entry_is_refused(tmp_path):
    """A cell entry that is not a FormKey is refused rather than silently dropped -- a dropped
    entry would quietly shrink the imported bound."""
    scope = _write(tmp_path, "bad-cell.json", _manifest(seeds=[], cells=["not-a-formkey"]))
    code, text = _run(tmp_path, scope)
    assert code == 2, f"expected exit 2, got {code}\n{_tail(text)}"
    assert "malformed declared cell" in (_scope_refusal(text) or "")


def test_empty_population_without_a_bound_is_refused(tmp_path):
    """records[] empty and nothing declared: still not a reusable scope."""
    scope = _write(tmp_path, "empty.json",
                   _manifest(seeds=[], cells=[], records=[], pairs=[]))
    code, text = _run(tmp_path, scope)
    assert code == 2, f"expected exit 2, got {code}\n{_tail(text)}"
    assert "declares no bound" in (_scope_refusal(text) or "")


# --- imported identities must be accounted for, not merely retained -------------------------

SCOPE_MANIFEST = os.environ.get("HOUSECARL_TEST_SCOPE_MANIFEST")
CAPTURED_MANIFEST = os.environ.get("HOUSECARL_TEST_CAPTURED_MANIFEST")
ACCOUNTING_AVAILABLE = bool(SCOPE_MANIFEST and CAPTURED_MANIFEST
                            and os.path.exists(SCOPE_MANIFEST or "")
                            and os.path.exists(CAPTURED_MANIFEST or ""))
ACCOUNTING_SKIP = "set HOUSECARL_TEST_SCOPE_MANIFEST and HOUSECARL_TEST_CAPTURED_MANIFEST"


class _Skip(Exception):
    """Raised by a data-driven case whose inputs were not supplied."""


def _accounting_inputs():
    if not ACCOUNTING_AVAILABLE:
        raise _Skip(ACCOUNTING_SKIP)
    return (json.load(open(SCOPE_MANIFEST, encoding="utf-8")),
            json.load(open(CAPTURED_MANIFEST, encoding="utf-8")))


def test_every_imported_pair_identity_is_retained():
    """The point of importing a scope is that a pair measured once keeps its row. A dropped pair
    identity is exactly the failure this change exists to prevent, so it is asserted over a real
    captured manifest rather than inferred from the code."""
    scope, captured = _accounting_inputs()
    imported = {p["pairKey"] for p in scope["pairs"]}
    retained = {p["pairKey"] for p in captured["pairs"]}
    dropped = imported - retained
    assert not dropped, \
        f"{len(dropped)} imported pair identities were dropped, e.g. {sorted(dropped)[:3]}"


DISPOSITIONS = ("emitted", "no_placement_history", "no_winning_pose", "out_of_scope")


def imported_identities(scope):
    """The identity union SceneCapture.ReadScope actually imports.

    BOTH sources, because the importer uses both: every records[] formid (SceneCapture.cs:76) and
    BOTH endpoints of every pairs[] row (SceneCapture.cs:112). Defining the population from records[]
    alone is how a pair-only endpoint disappears without any check noticing -- the identity is
    imported, so the capturing manifest owes it a disposition."""
    identities = {row["formid"] for row in scope.get("records") or []}
    for pair in scope.get("pairs") or []:
        identities.add(pair["a"])
        identities.add(pair["b"])
    return identities


def assert_imports_are_accounted(scope, captured):
    """THE INTEGRATION CONTRACT: the capturing manifest accounts for every identity its scope
    imported -- by enumerating it, or by naming it with a disposition.

    The receipt is checked against the ACTUAL import population, not only against itself. A receipt
    whose counts agree with one another can still be describing a different, smaller population than
    the one that was imported, and that shape is indistinguishable from a correct one until
    importedCount is compared to the real union."""
    imported = imported_identities(scope)
    enumerated = {row["formid"] for row in captured.get("records") or []}
    discovery = captured["scene"]["discovery"]
    declared_missing = set(discovery.get("missingImportedReferences") or [])
    accounting = discovery.get("importedReferenceAccounting")
    unmeasured = list((accounting or {}).get("unmeasured") or [])
    named = {row["formid"] for row in unmeasured}

    unaccounted = imported - enumerated - declared_missing - named
    assert not unaccounted, (
        f"{len(unaccounted)} of {len(imported)} imported identities are neither enumerated nor "
        f"given a disposition, e.g. {sorted(unaccounted)[:3]}")

    if accounting is None:
        return  # a manifest captured before the repair carries no receipt; read the older way

    assert accounting["importedCount"] == len(imported), (
        f"the receipt claims {accounting['importedCount']} imported identities but the scope "
        f"imports {len(imported)} (records[] plus both endpoints of every pairs[] row)")
    assert accounting["unaccounted"] == 0, \
        f"the receipt reports {accounting['unaccounted']} unaccounted identities"
    assert accounting["importedCount"] == sum(accounting["dispositions"].values()), \
        "the dispositions do not add up to the imported count"
    assert set(accounting["dispositions"]) == set(DISPOSITIONS), \
        f"unexpected disposition set: {sorted(accounting['dispositions'])}"

    # Exactly one disposition per identity: no row is classified twice, an unmeasured identity is
    # not also enumerated, and nothing is named that was never imported.
    assert len(named) == len(unmeasured), "an identity carries more than one unmeasured row"
    assert not (named & enumerated), \
        f"identities are both enumerated and unmeasured, e.g. {sorted(named & enumerated)[:3]}"
    assert named <= imported, \
        f"unmeasured names identities that were never imported, e.g. {sorted(named - imported)[:3]}"
    assert declared_missing <= named, \
        "missingImportedReferences is not drawn from the same partition as the unmeasured rows"
    assert all(row["disposition"] in DISPOSITIONS for row in unmeasured), \
        "an unmeasured row carries a disposition outside the declared set"
    assert accounting["dispositions"]["emitted"] == len(imported & enumerated), \
        "the emitted count does not match the imported identities the manifest enumerates"


def test_every_imported_reference_is_accounted_for():
    """The accounting read over a REAL captured pair, through the shared contract above.

    The boundary this cannot reach on its own (an identity with placement history but no usable
    pose, which the earlier receipt named nowhere because the record loop skipped on a null POSE
    while missingImportedReferences filtered on null PLACEMENT HISTORY) is exercised
    deterministically in tests/test_imported_reference_accounting.py, against the same producer
    partition -- as are the omitted-endpoint and false-receipt cases this data-driven case cannot
    manufacture.

    An identity named as unmeasured is NOT evidence it is gone, and no disposition says whether a
    record for it exists: every input is about placement."""
    scope, captured = _accounting_inputs()
    assert_imports_are_accounted(scope, captured)


def test_a_retained_pair_with_an_ineligible_member_is_not_reported_separated():
    """A member removed from the selected state must not make its pair read as 'separated'. The
    relation has to stay unknown with the member's own reason; separation is a measurement, and
    there is nothing to measure against a reference that is not there."""
    _, captured = _accounting_inputs()
    records = {r["formid"]: r for r in captured["records"]}
    ineligible = {k for k, r in records.items()
                  if (r.get("eligibility") or {}).get("status") != "eligible"}
    checked = 0
    for pair in captured["pairs"]:
        if pair["a"] not in ineligible and pair["b"] not in ineligible:
            continue
        shape = pair["actualShape"]
        checked += 1
        assert shape["intersects"] is None, (
            f"{pair['pairKey']} has an ineligible member but a measured verdict "
            f"intersects={shape['intersects']}")
        assert shape["surfaceRelation"] != "separated", \
            f"{pair['pairKey']} has an ineligible member but reads as separated"
        assert shape.get("reason"), f"{pair['pairKey']} is unknown with no stated reason"
    assert checked, "no retained pair had an ineligible member; the case was not exercised"


def _standalone():
    """Run the cases without pytest, so the contract stays checkable on a machine that has no
    test runner installed. Same cases, same assertions; pytest collects them unchanged."""
    import inspect
    import tempfile
    from pathlib import Path
    if UNAVAILABLE:
        print("SKIP:", SKIP_REASON)
        return 0
    cases = [v for k, v in sorted(globals().items()) if k.startswith("test_") and callable(v)]
    failures = skipped = 0
    for case in cases:
        needs_tmp = "tmp_path" in inspect.signature(case).parameters
        with tempfile.TemporaryDirectory() as td:
            try:
                case(Path(td)) if needs_tmp else case()
                print(f"PASS  {case.__name__}")
            except _Skip as exc:
                skipped += 1
                print(f"SKIP  {case.__name__}: {exc}")
            except AssertionError as exc:
                failures += 1
                print(f"FAIL  {case.__name__}: {exc}")
            except Exception as exc:  # noqa: BLE001 - a broken case is a failure, not a crash
                failures += 1
                print(f"ERROR {case.__name__}: {type(exc).__name__}: {exc}")
    print(f"{len(cases) - failures - skipped} passed, {failures} failed, {skipped} skipped")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(_standalone() if pytest is None else pytest.main([__file__, "-v"]))
