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
EXE = os.path.join(HARNESS, "overlapprobe", "bin", "Release", "net9.0", "overlapprobe.exe")
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
    """A synthetic cell is not a cell of the evaluated order, so once the bound reaches scoping
    the capture refuses on it with this text. That refusal is the PROOF the bound arrived: it
    names the cell and the scope file that declared it. A bound parsed and then dropped produces
    NO_BOUND_CARRIED instead, and a scope rejected outright produces a "could not read" line."""
    return (f"--scene-scope-from '{os.path.abspath(scope_path)}' declares cell {cell}, "
            "which has no placed reference from which to derive a bounded exterior footprint")


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
    """A capture declared by --scene-cell carries cells[] and an empty seedReferences[]. Refusing
    it was the defect. Accepting the file is only half the fix, so this asserts the exact refusal
    that can ONLY be reached once the imported cell has joined the capture's own cell set -- it
    names both the cell and the scope file that declared it."""
    scope = _write(tmp_path, "cell-only.json", _manifest(seeds=[], cells=[CELL_LOW]))
    code, text = _run(tmp_path, scope)
    assert _scope_refusal(text) is None, f"the scope must not be refused: {_scope_refusal(text)}"
    _assert_exact(code, text, expect_code=2,
                  expect_contains=unusable_cell_refusal(scope, CELL_LOW))


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
    _assert_exact(code, text, expect_code=2,
                  expect_contains=unusable_cell_refusal(scope, CELL_LOW))


# --- two scopes: both contributions must independently reach the bound ----------------------
#
# The capture stops at the first unusable cell, so one run can only witness one contribution.
# These two cases are a matched pair: two scopes are supplied both times, and only which scope
# owns the ordinally-lowest cell changes. Each run must name that scope's own cell and its own
# path. If either scope's cells were dropped from the merge, the run in which that scope owns the
# lowest cell would refuse on the other scope's cell instead -- so the pair fails.

def test_two_scopes_first_contribution_reaches_the_bound(tmp_path):
    a = _write(tmp_path, "scope-a.json", _manifest(seeds=[], cells=[CELL_LOW]))
    b = _write(tmp_path, "scope-b.json", _manifest(seeds=[], cells=[CELL_MID]))
    code, text = _run(tmp_path, a, b)
    _assert_exact(code, text, expect_code=2,
                  expect_contains=unusable_cell_refusal(a, CELL_LOW))
    assert CELL_MID not in text, "the refusal must name the ordinally-lowest declared cell"


def test_two_scopes_second_contribution_reaches_the_bound(tmp_path):
    """Same shape, but now the SECOND scope owns the lowest cell. Its bound must be the one
    reported, which the first case cannot show."""
    a = _write(tmp_path, "scope-a.json", _manifest(seeds=[], cells=[CELL_HIGH]))
    b = _write(tmp_path, "scope-b.json", _manifest(seeds=[], cells=[CELL_MID]))
    code, text = _run(tmp_path, a, b)
    _assert_exact(code, text, expect_code=2,
                  expect_contains=unusable_cell_refusal(b, CELL_MID))
    assert CELL_HIGH not in text, "the refusal must name the ordinally-lowest declared cell"


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


def test_every_imported_reference_is_enumerated_or_declared_missing():
    """Accounting check over a real captured manifest: every imported reference has a record row
    or is named in missingImportedReferences.

    DO NOT read this as proof that the two conditions cover each other. They do not:
    missingImportedReferences filters on missing HISTORY (Program.cs:1276) while the record loop
    skips on a missing POSE (Program.cs:961), so a reference with history but no usable pose is
    neither enumerated nor declared missing. This case passes on the retained fixture only because
    that fixture holds ZERO missing references, so it never reaches the boundary. A manifest that
    does exercise it belongs with the repair in
    Handoffs/ToDo/2026-09-16-retained-pair-absence-correction.md."""
    scope, captured = _accounting_inputs()
    imported = {r["formid"] for r in scope["records"]}
    enumerated = {r["formid"] for r in captured["records"]}
    declared_missing = set(captured["scene"]["discovery"].get("missingImportedReferences") or [])
    unaccounted = imported - enumerated - declared_missing
    assert not unaccounted, (
        f"{len(unaccounted)} imported references are neither enumerated nor declared missing, "
        f"e.g. {sorted(unaccounted)[:3]}")


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
