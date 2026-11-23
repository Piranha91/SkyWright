"""Known-answer tests for overlapprobe's declared-cell presence rule.

THE RULE: a cell declared by an imported scene scope exists in the evaluated setup only when some
plugin of that setup can declare its FormKey -- the owning plugin is loaded, or a loaded plugin lists
it as a master. Otherwise no plugin of the setup defines the cell, nothing is placed in it in game,
and it bounds nothing there: it is recorded as absent instead of refusing the capture. This is the
same rule overlap_attribution.py applies to objects (an object whose owning plugin a setup neither
loads nor masters is absent there), read from the same plugin list and masters.

A cell typed with --scene-cell is the operator's own bound; one naming a plugin the setup does not
load is refused rather than skipped.

These drive the REAL rule through the built binary (`--self-check-declared-cells FIXTURE`), which
calls the same DeclaredCellPresence the capture path calls. Identities are generic. Every case pins
the exit code.

Case numbers C600-C607. C607 is data-driven over a fixture extracted from a real capture pair and
skips loudly without HOUSECARL_TEST_P111_FIXTURE.
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

HARNESS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
sys.path.insert(0, HARNESS)
import overlap_attribution  # noqa: E402

# HOUSECARL_TEST_OVERLAPPROBE runs the cases against another build (a baseline or a sabotage arm).
EXE = os.environ.get("HOUSECARL_TEST_OVERLAPPROBE") or str(buildlayout.program("overlapprobe"))
KNOWN_ANSWER = os.environ.get("HOUSECARL_TEST_P111_FIXTURE")

BASE = "TestBase.esm"
LOADED = "TestLoaded.esp"
MASTERED_ONLY = "TestMasteredOnly.esm"
ABSENT = "TestAbsent.esp"
IMPORTED = "--scene-scope-from 'scope.json'"
TYPED = "--scene-cell"


def _plugins():
    return [{"Name": BASE, "Masters": []},
            {"Name": LOADED, "Masters": [BASE, MASTERED_ONLY]}]


def _run(fixture):
    with tempfile.TemporaryDirectory() as td:
        path = os.path.join(td, "fixture.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(fixture, f)
        proc = subprocess.run([EXE, "--self-check-declared-cells", path],
                              capture_output=True, text=True, timeout=300)
    return proc.returncode, proc.stdout, proc.stderr


def _check(fixture):
    code, out, err = _run(fixture)
    assert code == 0, f"exit {code}: {err.strip()[-400:]}"
    return json.loads(out)


def _outcomes(result):
    return {row["cell"]: row["outcome"] for row in result["cells"]}


@unittest.skipUnless(os.path.exists(EXE), "needs a built overlapprobe")
class DeclaredCellPresence(unittest.TestCase):
    def test_c600_owner_neither_loaded_nor_mastered_is_absent(self):
        cell = f"000D01:{ABSENT}"
        result = _check({"plugins": _plugins(), "declared": [{"cell": cell, "declaredBy": IMPORTED}]})
        self.assertEqual(_outcomes(result), {cell: "absent"})
        self.assertEqual(result["absentDeclaredCells"], [{"cell": cell, "owningPlugin": ABSENT,
            "declaredBy": IMPORTED, "basis": "owning-plugin-not-loaded"}])
        self.assertEqual((result["presentCount"], result["absentCount"], result["refuseCount"]), (0, 1, 0))

    def test_c601_loaded_owner_is_present(self):
        cell = f"000D02:{LOADED}"
        result = _check({"plugins": _plugins(), "declared": [{"cell": cell, "declaredBy": IMPORTED}]})
        self.assertEqual(_outcomes(result), {cell: "present"})
        self.assertEqual(result["absentDeclaredCells"], [])

    def test_c602_owner_mastered_but_not_loaded_is_present(self):
        cell = f"000D03:{MASTERED_ONLY}"
        result = _check({"plugins": _plugins(), "declared": [{"cell": cell, "declaredBy": IMPORTED}]})
        self.assertEqual(_outcomes(result), {cell: "present"})

    def test_c603_owner_matched_ignoring_case(self):
        cell = f"000D04:{LOADED.upper()}"
        result = _check({"plugins": _plugins(), "declared": [{"cell": cell, "declaredBy": IMPORTED}]})
        self.assertEqual(list(_outcomes(result).values()), ["present"])

    def test_c604_mixed_list_partition_is_total_and_ordered(self):
        declared = [f"000E09:{ABSENT}", f"000E01:{LOADED}", f"000E05:{ABSENT}", f"000E03:{MASTERED_ONLY}"]
        result = _check({"plugins": _plugins(), "declared": [{"cell": c, "declaredBy": IMPORTED} for c in declared]})
        cells = [row["cell"] for row in result["cells"]]
        self.assertEqual(sorted(cells), sorted(declared))
        self.assertEqual(len(cells), len(set(cells)))
        self.assertEqual(cells, sorted(cells))
        self.assertEqual(result["presentCount"] + result["absentCount"] + result["refuseCount"], len(declared))
        self.assertEqual([r["cell"] for r in result["absentDeclaredCells"]],
                         sorted(c for c in declared if c.endswith(ABSENT)))

    def test_c605_same_answer_as_the_attribution_rule(self):
        declared = [f"000F01:{ABSENT}", f"000F02:{LOADED}", f"000F03:{MASTERED_ONLY}", f"000F04:{BASE.lower()}"]
        plugins = _plugins()
        result = _check({"plugins": plugins, "declared": [{"cell": c, "declaredBy": IMPORTED} for c in declared]})
        manifest = {"scope": {"configuration": {"Plugins": plugins}}}
        declarable = overlap_attribution._declarable_owners(manifest)
        expected_absent = {c for c in declared if overlap_attribution._owner_not_loaded(c, declarable) is not None}
        self.assertEqual({c for c, o in _outcomes(result).items() if o == "absent"}, expected_absent)
        self.assertEqual(expected_absent, {f"000F01:{ABSENT}"})

    def test_c606_typed_scene_cell_with_absent_owner_is_refused_not_skipped(self):
        cell = f"000D06:{ABSENT}"
        result = _check({"plugins": _plugins(), "declared": [{"cell": cell, "declaredBy": TYPED}]})
        self.assertEqual(_outcomes(result), {cell: "refuse"})
        self.assertEqual(result["absentDeclaredCells"], [])

    @unittest.skipUnless(KNOWN_ANSWER and os.path.exists(KNOWN_ANSWER or ""),
                         "set HOUSECARL_TEST_P111_FIXTURE to a known-answer fixture extracted from a real capture pair")
    def test_c607_known_answer_from_a_real_capture_pair(self):
        """The fixture holds a real seed scene's declared cells, the plugin lists of the setups that imported it, and the
        counts computed independently from those files. The candidate-off setup must find every candidate-owned cell
        absent; the setup that loads the candidate must find none absent."""
        with open(KNOWN_ANSWER, encoding="utf-8") as f:
            known = json.load(f)
        for setup, expect in known["expect"].items():
            result = _check(known["setups"][setup])
            self.assertEqual((result["absentCount"], result["presentCount"], result["refuseCount"]),
                             (expect["absent"], expect["present"], 0), setup)
            self.assertEqual(sorted({r["owningPlugin"] for r in result["absentDeclaredCells"]}),
                             sorted(expect["absentOwners"]), setup)


if __name__ == "__main__":
    unittest.main(verbosity=2)
