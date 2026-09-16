"""Known-answer tests for overlapprobe's imported-reference accounting.

THE CONTRACT: when a capture imports a scene scope, every imported reference identity receives
exactly one disposition in the manifest. An identity is `emitted` (a record row enumerates it),
`no_placement_history` (no plugin in the order gives it a placement -- the population
`missingImportedReferences` has always named), `no_winning_pose` (it has placement history but no
winning placement resolved, so its position and geometry are unknown here) or `out_of_scope` (it has
a placement but lies outside this capture's bound). The partition is total: nothing falls through.

Every input is about PLACEMENT. `no_placement_history` does NOT mean the order declares no record:
a placed record with no Placement subrecord -- which a deletion typically strips -- is dropped by
PlacementExtract.ToRef before it reaches the history, so an identity whose only record is
placement-less lands there while a record for it exists.

WHY IT MATTERS: the receipt previously answered with two filters that are not complementary -- the
record loop skips on a null POSE, `missingImportedReferences` filters on null PLACEMENT HISTORY -- so an
identity with history and no usable pose was named nowhere at all. A consumer counting rows could
then only guess about it, and guessing in that direction means reading a reference's failure to
appear as evidence it is gone. No disposition asserts presence or absence in the game state; an
earned absence comes from an enumerated record row's own fields, never from a missing row.

These drive the REAL partition through the built overlapprobe binary
(`--self-check-imported-accounting FIXTURE`), which runs the same Classify/Partition/Receipt the
capture path runs -- there is no second implementation here to drift from the producer. The
fixture supplies the four inputs a real capture resolves (imported identities, which have a record
row, which have history, which have a winning pose), so the boundary is reachable without a load
order. Identities are generic: no real cell, plugin or reference appears.

Every case pins the process exit code. Asserting only on output text would let an unrelated
failure -- a crash, a bad path, a missing binary -- pass as success, which is not a test.
"""
import json
import os
import subprocess
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
# The scope-import suite owns the integration contract (what the importer actually imports, and what
# a capturing manifest then owes it). Imported rather than restated, so the real captured pair and
# the deterministic cases below are checked against ONE definition.
import test_scene_scope_import as scope_import  # noqa: E402

HARNESS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(HARNESS, "overlapprobe", "bin", "Release", "net9.0", "overlapprobe.exe")

# Generic identities. EMITTED is an ordinary measured reference; EXCLUDED stands for a member that
# IS enumerated but sits outside the selected state (disabled or deleted) -- it has a row, so the
# accounting must reach it through that row and never through omission.
EMITTED = "000C01:TestScope.esm"
EXCLUDED = "000C02:TestScope.esm"
NO_POSE = "000C03:TestScope.esm"
NO_HISTORY = "000C04:TestScope.esm"
OUT_OF_SCOPE = "000C05:TestScope.esm"


def _fixture(tmpdir, name="fixture.json", **overrides):
    """The four conditions in one fixture, unless a case overrides a member."""
    payload = {
        "imported": [EMITTED, EXCLUDED, NO_POSE, NO_HISTORY, OUT_OF_SCOPE],
        # A disabled/deleted member still has a record row; that is how it is accounted.
        "emitted": [EMITTED, EXCLUDED],
        "history": [EMITTED, EXCLUDED, NO_POSE, OUT_OF_SCOPE],
        "pose": [EMITTED, EXCLUDED, OUT_OF_SCOPE],
    }
    payload.update(overrides)
    path = os.path.join(tmpdir, name)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle)
    return path


@unittest.skipUnless(os.path.exists(EXE), f"needs a built overlapprobe at {EXE}")
class ImportedReferenceAccounting(unittest.TestCase):
    maxDiff = None

    def setUp(self):
        import tempfile
        self._tmp = tempfile.TemporaryDirectory()
        self.tmpdir = self._tmp.name
        self.addCleanup(self._tmp.cleanup)

    def run_probe(self, fixture_path):
        proc = subprocess.run([EXE, "--self-check-imported-accounting", fixture_path],
                              capture_output=True, text=True, timeout=300)
        return proc.returncode, proc.stdout, proc.stderr

    def receipt(self, **overrides):
        code, out, err = self.run_probe(_fixture(self.tmpdir, **overrides))
        self.assertEqual(0, code, f"self-check exited {code}\nstdout:\n{out}\nstderr:\n{err}")
        return json.loads(out)

    # ---- the partition is total -------------------------------------------------------
    def test_every_imported_identity_gets_exactly_one_disposition(self):
        got = self.receipt()["importedReferenceAccounting"]
        self.assertEqual(5, got["importedCount"])
        self.assertEqual(0, got["unaccounted"])
        self.assertEqual(got["importedCount"], sum(got["dispositions"].values()),
                         "the dispositions must account for every imported identity")
        named = [row["formid"] for row in got["unmeasured"]]
        self.assertEqual(len(named), len(set(named)), "an identity is classified once, not twice")

    def test_the_dispositions_are_the_declared_set(self):
        got = self.receipt()["importedReferenceAccounting"]
        self.assertEqual({"emitted", "no_placement_history", "no_winning_pose", "out_of_scope"},
                         set(got["dispositions"]))
        self.assertEqual(set(scope_import.DISPOSITIONS), set(got["dispositions"]),
                         "the producer and the integration contract must name the same dispositions")
        self.assertTrue(all(row["disposition"] in got["dispositions"] for row in got["unmeasured"]))

    # ---- the boundary this repair exists for ------------------------------------------
    def test_history_without_a_usable_pose_is_named_and_is_not_called_missing(self):
        """The defect: an identity with winner history and no winning pose satisfied neither the
        record loop nor missingImportedReferences, so the manifest named it nowhere."""
        got = self.receipt()
        rows = {row["formid"]: row for row in got["importedReferenceAccounting"]["unmeasured"]}
        self.assertIn(NO_POSE, rows, "the history/no-pose identity must be named")
        self.assertEqual("no_winning_pose", rows[NO_POSE]["disposition"])
        self.assertNotIn(NO_POSE, got["missingImportedReferences"],
                         "it HAS history, so it is not a missing-from-the-order identity")
        self.assertTrue(rows[NO_POSE]["reason"], "a disposition states its reason")

    def test_no_placement_history_is_exactly_the_missing_imported_references_population(self):
        """missingImportedReferences keeps its meaning and is now derived from the same partition,
        so the two can no longer disagree about the same reference."""
        got = self.receipt()
        self.assertEqual([NO_HISTORY], got["missingImportedReferences"])
        rows = {row["formid"]: row for row in got["importedReferenceAccounting"]["unmeasured"]}
        self.assertEqual("no_placement_history", rows[NO_HISTORY]["disposition"])

    def test_the_no_placement_history_reason_does_not_claim_the_record_is_undeclared(self):
        """It knows placement, not declaration: a placement-less record still declares the identity
        (PlacementExtract.ToRef drops it before the history sees it). The reason must not say the
        order declares the reference nowhere."""
        rows = {row["formid"]: row for row in
                self.receipt()["importedReferenceAccounting"]["unmeasured"]}
        reason = rows[NO_HISTORY]["reason"]
        self.assertIn("placement", reason)
        self.assertNotIn("declares the reference,", reason)

    def test_an_out_of_scope_identity_is_distinct_from_one_the_order_never_had(self):
        got = self.receipt()
        rows = {row["formid"]: row for row in got["importedReferenceAccounting"]["unmeasured"]}
        self.assertEqual("out_of_scope", rows[OUT_OF_SCOPE]["disposition"])
        self.assertNotIn(OUT_OF_SCOPE, got["missingImportedReferences"])

    # ---- an enumerated reference is accounted by its row, excluded or not --------------
    def test_a_measured_reference_is_accounted_as_emitted(self):
        got = self.receipt()["importedReferenceAccounting"]
        named = {row["formid"] for row in got["unmeasured"]}
        self.assertNotIn(EMITTED, named, "an enumerated reference needs no unmeasured row")
        self.assertEqual(2, got["dispositions"]["emitted"])

    def test_a_state_excluded_member_is_accounted_by_its_record_row(self):
        """A disabled or deleted member still has a record row -- its exclusion is stated in that
        row's own fields. It must not appear as an unmeasured or missing identity, which is how a
        consumer would end up inferring absence from omission instead of reading the row."""
        got = self.receipt()
        named = {row["formid"] for row in got["importedReferenceAccounting"]["unmeasured"]}
        self.assertNotIn(EXCLUDED, named)
        self.assertNotIn(EXCLUDED, got["missingImportedReferences"])

    # ---- the accounting cannot claim a row the manifest does not hold ------------------
    def test_a_fixture_that_contradicts_the_producer_is_refused(self):
        """The record loop only emits a row for a reference it resolved a pose for. A fixture
        saying otherwise would exercise a state the producer cannot reach, so it is refused rather
        than quietly classified -- a self-check that accepts an impossible input proves nothing."""
        path = _fixture(self.tmpdir, name="contradiction.json",
                        pose=[EMITTED, OUT_OF_SCOPE])          # EXCLUDED is emitted with no pose
        code, out, err = self.run_probe(path)
        self.assertEqual(2, code, f"expected a refusal, got {code}\nstdout:\n{out}\nstderr:\n{err}")
        self.assertIn("no winning pose", err)

    def test_a_row_that_is_emitted_but_never_imported_is_refused(self):
        path = _fixture(self.tmpdir, name="stray.json",
                        imported=[EXCLUDED, NO_POSE, NO_HISTORY, OUT_OF_SCOPE])
        code, out, err = self.run_probe(path)
        self.assertEqual(2, code, f"expected a refusal, got {code}\nstdout:\n{out}\nstderr:\n{err}")
        self.assertIn("not imported", err)

    def test_a_malformed_fixture_is_refused_rather_than_partially_read(self):
        path = os.path.join(self.tmpdir, "malformed.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump({"imported": [EMITTED], "emitted": [], "history": [EMITTED]}, handle)
        code, out, err = self.run_probe(path)
        self.assertEqual(2, code, f"expected a refusal, got {code}\nstdout:\n{out}\nstderr:\n{err}")
        self.assertIn("pose", err)


class ImportedIdentityCoverage(unittest.TestCase):
    """THE INTEGRATION CONTRACT, deterministically: does the accounting check actually notice when
    an imported identity goes missing?

    The producer cases above prove the classifier partitions whatever it is GIVEN. They say nothing
    about whether the population handed to it is the population the importer built. SceneCapture
    imports every records[] formid AND both endpoints of every pairs[] row, so a check defined over
    records[] alone passes while a pair-only endpoint disappears -- and a receipt whose totals agree
    with each other can still be describing a smaller population than the one imported.

    These drive the SHARED helpers in test_scene_scope_import.py -- the same ones the real captured
    pair is read through -- so a regression in the contract fails here, on every run, without an
    MO2 target. No binary, no game, no load order.
    """

    def scope(self, records=(), pairs=()):
        return {"records": [{"formid": f} for f in records],
                "pairs": [{"pairKey": a + "||" + b, "a": a, "b": b} for a, b in pairs]}

    def captured(self, records=(), missing=(), accounting=None):
        discovery = {"missingImportedReferences": list(missing)}
        if accounting is not None:
            discovery["importedReferenceAccounting"] = accounting
        return {"records": [{"formid": f} for f in records], "scene": {"discovery": discovery}}

    def receipt(self, imported_count, emitted=0, no_placement_history=0, no_winning_pose=0,
                out_of_scope=0, unmeasured=()):
        return {"importedCount": imported_count,
                "dispositions": {"emitted": emitted, "no_placement_history": no_placement_history,
                                 "no_winning_pose": no_winning_pose, "out_of_scope": out_of_scope},
                "unaccounted": 0,
                "unmeasured": [{"formid": f, "disposition": d, "reason": "fixture"}
                               for f, d in unmeasured]}

    def test_the_import_population_includes_pair_endpoints(self):
        """The population rule itself: SceneCapture.cs:76 adds records[], :112 adds both endpoints."""
        got = scope_import.imported_identities(self.scope(records=[EMITTED],
                                                          pairs=[(NO_POSE, OUT_OF_SCOPE)]))
        self.assertEqual({EMITTED, NO_POSE, OUT_OF_SCOPE}, got)

    def test_a_pair_only_endpoint_that_vanishes_is_caught(self):
        """The review's reproduction, as a test: a scope whose identities are pair endpoints only,
        and a capture that enumerates neither and reports a zero-import receipt. Defining the
        population from records[] alone made this PASS."""
        scope = self.scope(pairs=[(NO_POSE, OUT_OF_SCOPE)])
        captured = self.captured(accounting=self.receipt(0))
        with self.assertRaises(AssertionError) as caught:
            scope_import.assert_imports_are_accounted(scope, captured)
        self.assertIn("neither enumerated nor given a disposition", str(caught.exception))

    def test_a_receipt_that_counts_the_wrong_population_is_caught(self):
        """Self-consistent and false: every identity IS named, the dispositions add up and
        unaccounted is 0 -- but importedCount describes a population of one, not three. Comparing
        the receipt's totals only to each other cannot see this."""
        scope = self.scope(records=[EMITTED], pairs=[(NO_POSE, OUT_OF_SCOPE)])
        captured = self.captured(
            records=[EMITTED],
            accounting=self.receipt(1, emitted=1,
                                    unmeasured=[(NO_POSE, "no_winning_pose"),
                                                (OUT_OF_SCOPE, "out_of_scope")]))
        with self.assertRaises(AssertionError) as caught:
            scope_import.assert_imports_are_accounted(scope, captured)
        self.assertIn("imported identities but the scope imports", str(caught.exception))

    def test_an_identity_both_enumerated_and_unmeasured_is_caught(self):
        """Exactly one disposition per identity is the whole claim; two is not a rounding error."""
        scope = self.scope(records=[EMITTED, NO_POSE])
        captured = self.captured(
            records=[EMITTED, NO_POSE],
            accounting=self.receipt(2, emitted=2, unmeasured=[(NO_POSE, "no_winning_pose")]))
        with self.assertRaises(AssertionError) as caught:
            scope_import.assert_imports_are_accounted(scope, captured)
        self.assertIn("both enumerated and unmeasured", str(caught.exception))

    def test_a_missing_list_drawn_from_outside_the_partition_is_caught(self):
        """missingImportedReferences must be the partition's own no_placement_history rows. A list
        assembled separately is how the two filters drifted apart in the first place."""
        scope = self.scope(records=[EMITTED, NO_HISTORY])
        captured = self.captured(
            records=[EMITTED], missing=[NO_HISTORY],
            accounting=self.receipt(2, emitted=1, no_placement_history=1))   # unmeasured left empty
        with self.assertRaises(AssertionError) as caught:
            scope_import.assert_imports_are_accounted(scope, captured)
        self.assertIn("not drawn from the same partition", str(caught.exception))

    def test_a_correct_receipt_over_the_full_union_is_accepted(self):
        """The GREEN control: without it the four cases above would pass on a helper that rejects
        everything."""
        scope = self.scope(records=[EMITTED, NO_HISTORY], pairs=[(NO_POSE, OUT_OF_SCOPE)])
        captured = self.captured(
            records=[EMITTED], missing=[NO_HISTORY],
            accounting=self.receipt(4, emitted=1, no_placement_history=1, no_winning_pose=1,
                                    out_of_scope=1,
                                    unmeasured=[(NO_HISTORY, "no_placement_history"),
                                                (NO_POSE, "no_winning_pose"),
                                                (OUT_OF_SCOPE, "out_of_scope")]))
        scope_import.assert_imports_are_accounted(scope, captured)      # must not raise

    def test_a_manifest_captured_before_the_repair_is_read_the_older_way(self):
        """A pre-repair manifest carries no receipt. It is still required to account for every
        imported identity, through the record rows and missingImportedReferences it does have."""
        scope = self.scope(records=[EMITTED, NO_HISTORY])
        scope_import.assert_imports_are_accounted(
            scope, self.captured(records=[EMITTED], missing=[NO_HISTORY]))
        with self.assertRaises(AssertionError):
            scope_import.assert_imports_are_accounted(scope, self.captured(records=[EMITTED]))


if __name__ == "__main__":
    unittest.main()
