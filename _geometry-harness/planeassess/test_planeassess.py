"""Known answers for planeassess's records steps: edit kinds, record classes, the shape difference and the
geometry self-test. python -B -m pytest scripts/planeassess/test_planeassess.py (or run it directly)."""
import copy
import json
import os
import math
import struct
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import planeassess as pa  # noqa: E402


def row(**kw):
    r = {"formKey": "000800:Patch.esp", "sig": "REFR", "flagsRaw": 0, "deleted": False, "initiallyDisabled": False,
         "base": pa.PLANE_MARKER, "pos": [100.0, 200.0, 300.0], "rot": [0.0, 0.0, 1.0], "scale": 1.0,
         "prim": {"bounds": [100.0, 1.0, 50.0], "colour": [1.0, 0.0, 0.0], "unknown": 0.0, "typeRaw": 3},
         "enableParent": None, "occlusionPlane": None, "teleport": None, "cell": None, "worldspace": "00003C:Skyrim.esm"}
    r.update(kw)
    return r


def disabled_idiom(r):
    d = copy.deepcopy(r)
    d["initiallyDisabled"], d["flagsRaw"] = True, pa.FLAG_INITIALLY_DISABLED
    d["enableParent"] = {"ref": pa.PLAYER, "flagsRaw": 1}
    d["pos"] = [d["pos"][0], d["pos"][1], -30000.0]
    return d


class EditKinds(unittest.TestCase):
    def test_add(self):
        self.assertEqual(pa.edit_kinds(None, row()), ["ADD"])

    def test_disable_absorbs_idiom(self):
        self.assertEqual(pa.edit_kinds(row(), disabled_idiom(row())), ["DISABLE"])

    def test_move_rotate_bounds(self):
        p = row(pos=[101.0, 200.0, 300.0], rot=[0.0, 0.0, 1.1])
        p["prim"] = dict(p["prim"], bounds=[120.0, 1.0, 50.0])
        self.assertEqual(pa.edit_kinds(row(), p), ["MOVE", "ROTATE", "BOUNDS"])

    def test_rotation_equal_modulo_two_pi(self):
        self.assertEqual(pa.edit_kinds(row(), row(rot=[0.0, 0.0, 1.0 + 2 * math.pi])), [])

    def test_position_within_tolerance(self):
        self.assertEqual(pa.edit_kinds(row(), row(pos=[100.00005, 200.0, 300.0])), [])

    def test_enable(self):
        self.assertEqual(pa.edit_kinds(disabled_idiom(row()), row()), ["ENABLE", "MOVE"])


class Classes(unittest.TestCase):
    def test_new(self):
        self.assertEqual(pa.classify(None, row(), []), "new")

    def test_restored_to_other_plugin(self):
        b = row(pos=[0.0, 0.0, 0.0])
        self.assertEqual(pa.classify(row(), copy.deepcopy(b), [("B.esp", b)]), "restored to B.esp's version")

    def test_moved(self):
        self.assertEqual(pa.classify(row(), row(pos=[0.0, 0.0, 0.0]), []), "moved-or-resized")

    def test_disabled_in_both(self):
        u = disabled_idiom(row())
        p = copy.deepcopy(u)
        p["enableParent"] = None
        self.assertEqual(pa.classify(u, p, []), "disabled in both")


class ShapeDiffers(unittest.TestCase):
    def test_colour_only_is_not_a_difference(self):
        p = row()
        p["prim"] = dict(p["prim"], colour=[0.0, 1.0, 0.0])
        self.assertFalse(pa.shape_differs(row(), p))

    def test_both_disabled_is_not_a_difference(self):
        self.assertFalse(pa.shape_differs(disabled_idiom(row()), disabled_idiom(row(pos=[0.0, 0.0, 0.0]))))

    def test_disable_and_move_are_differences(self):
        self.assertTrue(pa.shape_differs(row(), disabled_idiom(row())))
        self.assertTrue(pa.shape_differs(row(), row(pos=[0.0, 0.0, 0.0])))
        self.assertTrue(pa.shape_differs(None, row()))

    def test_type_change_is_a_difference(self):
        p = row()
        p["prim"] = dict(p["prim"], typeRaw=1)
        self.assertTrue(pa.shape_differs(row(), p))


class Geometry(unittest.TestCase):
    def test_xprm_bounds_are_full_extents(self):
        # viewprobe's PlaneGeometry halves XPRM bounds; reading them as half extents doubles every plane
        self.assertEqual(pa.shape(row(scale=2.0))["half"], [100.0, 1.0, 50.0])

    def test_selftest_passes(self):
        self.assertEqual(pa.geometry_selftest(verbose=False), [])


class Unrendered(unittest.TestCase):
    def test_no_culling_change_is_set_aside_from_the_records(self):
        v = pa.unrendered_verdict("no change to the culling shape viewprobe reads (colour, XOCP or a disabled record's parent only)", "Box")
        self.assertEqual((v["verdict"], v["reason"]), ("set aside", "no change to what is culled (records)"))

    def test_tilted_refusal_reads_tilted(self):
        v = pa.unrendered_verdict("rule refuses the shape at every gaze that sees it: PortalBox tilted (12)", "PortalBox")
        self.assertEqual((v["verdict"], v["reason"]), ("cannot tell", "rule refuses a tilted plane"))


class OutOfMemory(unittest.TestCase):
    def test_heap_cap_refusal_is_found_in_the_log(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / "x.log"
            f.write_text("rendering\nOut of memory.\n", encoding="utf-8")
            self.assertTrue(pa.out_of_memory(f))
            f.write_text("System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.",
                         encoding="utf-8")
            self.assertTrue(pa.out_of_memory(f))
            f.write_text("rendering\ndone\n", encoding="utf-8")
            self.assertFalse(pa.out_of_memory(f))


class Reader(unittest.TestCase):
    def test_reads_a_placed_primitive_in_a_worldspace(self):
        def sub(sig, data):
            return sig + struct.pack("<H", len(data)) + data
        tes4 = sub(b"HEDR", struct.pack("<fiI", 1.7, 1, 0x800)) + sub(b"MAST", b"Skyrim.esm\x00")
        refr_body = (sub(b"NAME", struct.pack("<I", 0x17)) + sub(b"DATA", struct.pack("<6f", 1, 2, 3, 0, 0, 0.5)) +
                     sub(b"XPRM", struct.pack("<3f3ffI", 10, 1, 20, 1, 0, 0, 0, 3)))
        refr = b"REFR" + struct.pack("<IIIII", len(refr_body), 0, 0x01000800, 0, 0) + refr_body
        cell_body = b""
        cell = b"CELL" + struct.pack("<IIIII", 0, 0, 0x0000ABCD, 0, 0)
        temp = b"GRUP" + struct.pack("<IIiI", 24 + len(refr), 0x0000ABCD, 9, 0) + struct.pack("<I", 0) + refr
        wrld_children = b"GRUP" + struct.pack("<IIiI", 24 + len(cell) + len(temp), 0x3C, 1, 0) + struct.pack("<I", 0) + cell + temp
        head = b"TES4" + struct.pack("<IIIII", len(tes4), 0, 0, 0, 0) + tes4
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "Patch.esp"
            path.write_bytes(head + wrld_children + cell_body)
            rows = list(pa.Plugin(path).placed())
        self.assertEqual(len(rows), 1)
        r = rows[0]
        self.assertEqual((r["formKey"], r["base"], r["worldspace"], r["cell"]),
                         ("000800:Patch.esp", "000017:Skyrim.esm", "00003C:Skyrim.esm", "00ABCD:Skyrim.esm"))
        self.assertEqual((pa.kind_of(r), r["prim"]["bounds"], r["pos"]), ("PortalBox", [10.0, 1.0, 20.0], [1.0, 2.0, 3.0]))



class Resume(unittest.TestCase):
    IDENT = {"viewprobeDll": "v1", "coreDll": "c1", "rule": "r1", "order": "o1", "arm": "u"}

    def _out(self, d, rows, reports):
        out = Path(d)
        with (out / "ledger.jsonl").open("w", encoding="utf-8") as fh:
            for r in rows:
                fh.write(json.dumps(r) + "\n")
        for chunk, key, gz in reports:
            (out / chunk).mkdir(exist_ok=True)
            (out / chunk / ("cell-c1s1-%s-fp-planes.json%s" % (key, ".gz" if gz else ""))).write_text("{}", encoding="utf-8")
        return out

    def test_finished_shots_with_the_same_identity_are_skipped(self):
        with tempfile.TemporaryDirectory() as d:
            out = self._out(d, [dict(self.IDENT, chunk="c1", shots=["G-A-1", "G-A-2"], exitCode=0, stopped=None),
                                dict(self.IDENT, chunk="c2", shots=["G-B-1"], exitCode=3, stopped=None)],
                            [("c1", "G_A_1", True), ("c1", "G_A_2", False), ("c2", "G_B_1", False)])
            self.assertEqual(pa.done_shots(out, self.IDENT), {"G-A-1", "G-A-2", "G-B-1"})

    def test_partial_or_foreign_chunks_are_rendered_again(self):
        with tempfile.TemporaryDirectory() as d:
            out = self._out(d, [dict(self.IDENT, chunk="c1", shots=["G-A-1", "G-A-2"], exitCode=0, stopped=None),
                                dict(self.IDENT, chunk="c2", shots=["G-B-1"], exitCode=None, stopped="deadline"),
                                dict(self.IDENT, chunk="c3", shots=["G-C-1"], exitCode=0, stopped=None, viewprobeDll="v0"),
                                {"chunk": "c1", "event": "heap-cap Out of memory"}],
                            [("c1", "G_A_1", True), ("c2", "G_B_1", False), ("c3", "G_C_1", False)])
            self.assertEqual(pa.done_shots(out, self.IDENT), {"G-A-1"})   # G-A-2's report is missing


class Lock(unittest.TestCase):
    def test_stale_lock_is_named_with_its_pid(self):
        with tempfile.TemporaryDirectory() as d:
            lk = Path(d) / "HEAVY-LOCK"
            lk.write_text(json.dumps({"pid": 999999991, "purpose": "x"}), encoding="utf-8")
            msg = pa.lock_problem(lk)
            self.assertIn("999999991", msg)
            self.assertIn("stale", msg)
            self.assertIn("by hand", msg)

    def test_live_lock_and_no_lock(self):
        with tempfile.TemporaryDirectory() as d:
            lk = Path(d) / "HEAVY-LOCK"
            self.assertIsNone(pa.lock_problem(lk))
            lk.write_text(json.dumps({"pid": os.getpid(), "purpose": "x"}), encoding="utf-8")
            self.assertIn("held by running pid %d" % os.getpid(), pa.lock_problem(lk))

class DepthMeasurement(unittest.TestCase):
    """render --depth-measurement: the flag reaches viewprobe, the identity names it only when on, default and mode
    renders never resume each other, an old renderer is refused before the block, and a report that does not record
    the mode is caught."""
    OLD = ("import sys\nsys.stderr.write(\"viewprobe does not recognise '--depth-measurement'. Either this build has "
           "no such flag, or it is a flag whose VALUE is missing.\\n\")\nsys.exit(2)\n")
    NEW = ("import sys\nsys.stderr.write(\"--depth-measurement only applies to a --planes-report run; add "
           "--planes-report or drop the flag.\\n\")\nsys.exit(1)\n")

    def _argv(self, on):
        return pa.chunk_argv("vp.exe", "wl.json", "inst", "prof", "game", "order.json", "rule.json", "dest", False, on)

    def test_flag_reaches_the_renderer_only_when_asked(self):
        self.assertIn("--depth-measurement", self._argv(True))
        self.assertNotIn("--depth-measurement", self._argv(False))
        self.assertEqual([x for x in self._argv(True) if x != "--depth-measurement"], self._argv(False))

    def test_identity_names_the_mode_only_when_on(self):
        base = dict(Resume.IDENT)
        self.assertEqual(pa.with_mode(base, False), base)
        self.assertEqual(pa.with_mode(base, True), dict(base, measurementMode="depth-measurement"))
        self.assertIn("measurementMode", pa.RESUME_KEYS)

    def test_default_and_mode_renders_never_resume_each_other(self):
        default, mode = dict(Resume.IDENT), pa.with_mode(dict(Resume.IDENT), True)
        with tempfile.TemporaryDirectory() as d:
            out = Resume._out(self, d, [dict(default, chunk="c1", shots=["G-A-1"], exitCode=0, stopped=None),
                                        dict(mode, chunk="c2", shots=["G-B-1"], exitCode=0, stopped=None)],
                              [("c1", "G_A_1", False), ("c2", "G_B_1", False)])
            self.assertEqual(pa.done_shots(out, default), {"G-A-1"})
            self.assertEqual(pa.done_shots(out, mode), {"G-B-1"})

    def _fake(self, d, body):
        f = Path(d) / "fake_vp.py"
        f.write_text(body, encoding="utf-8")
        return [sys.executable, str(f)]

    def test_an_old_renderer_is_refused_before_the_block(self):
        with tempfile.TemporaryDirectory() as d:
            msg = pa.depth_measurement_problem(self._fake(d, self.OLD))
            self.assertIsNotNone(msg)
            self.assertIn("does not accept --depth-measurement", msg)

    def test_a_renderer_with_the_flag_passes_the_preflight(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIsNone(pa.depth_measurement_problem(self._fake(d, self.NEW)))

    def test_a_report_that_does_not_record_the_mode_is_caught(self):
        with tempfile.TemporaryDirectory() as d:
            ok, bad = Path(d) / "ok-fp-planes.json", Path(d) / "bad-fp-planes.json"
            ok.write_text(json.dumps({"appearance": {"mode": "depth-measurement",
                                                     "measurementMode": {"name": "depth-measurement"}}}), encoding="utf-8")
            bad.write_text(json.dumps({"appearance": {"status": "unassessed"}}), encoding="utf-8")
            self.assertTrue(pa.report_records_mode(ok))
            self.assertFalse(pa.report_records_mode(bad))


class OcclusionQuery(unittest.TestCase):
    """render --occlusion-query: the flag reaches viewprobe only with --depth-measurement, the identity names it only
    when on, renders with and without it never resume each other, an old renderer is refused before the block, and a
    report that does not record the query is caught."""
    OLD = ("import sys\nsys.stderr.write(\"viewprobe does not recognise '--occlusion-query'. Either this build has "
           "no such flag, or it is a flag whose VALUE is missing.\\n\")\nsys.exit(2)\n")
    NEW = ("import sys\nsys.stderr.write(\"--depth-measurement only applies to a --planes-report run; add "
           "--planes-report or drop the flag.\\n\")\nsys.exit(1)\n")

    def _argv(self, query):
        return pa.chunk_argv("vp.exe", "wl.json", "inst", "prof", "game", "order.json", "rule.json", "dest", False, True,
                             query)

    def test_flag_reaches_the_renderer_only_when_asked(self):
        self.assertIn("--occlusion-query", self._argv(True))
        self.assertNotIn("--occlusion-query", self._argv(False))
        self.assertEqual([x for x in self._argv(True) if x != "--occlusion-query"], self._argv(False))
        self.assertIn("--depth-measurement", self._argv(True))

    def test_the_query_without_the_mode_is_refused(self):
        self.assertIsNotNone(pa.occlusion_query_flags_problem(False, True))
        self.assertIn("--depth-measurement", pa.occlusion_query_flags_problem(False, True))
        self.assertIsNone(pa.occlusion_query_flags_problem(True, True))
        self.assertIsNone(pa.occlusion_query_flags_problem(True, False))
        self.assertIsNone(pa.occlusion_query_flags_problem(False, False))

    def test_identity_names_the_query_only_when_on(self):
        base = dict(Resume.IDENT)
        self.assertEqual(pa.with_mode(base, True, False), dict(base, measurementMode="depth-measurement"))
        self.assertEqual(pa.with_mode(base, True, True), dict(base, measurementMode="depth-measurement", occlusionQuery=True))
        self.assertIn("occlusionQuery", pa.RESUME_KEYS)

    def test_mode_and_query_renders_never_resume_each_other(self):
        mode, query = pa.with_mode(dict(Resume.IDENT), True, False), pa.with_mode(dict(Resume.IDENT), True, True)
        with tempfile.TemporaryDirectory() as d:
            out = Resume._out(self, d, [dict(mode, chunk="c1", shots=["G-A-1"], exitCode=0, stopped=None),
                                        dict(query, chunk="c2", shots=["G-B-1"], exitCode=0, stopped=None)],
                              [("c1", "G_A_1", False), ("c2", "G_B_1", False)])
            self.assertEqual(pa.done_shots(out, mode), {"G-A-1"})
            self.assertEqual(pa.done_shots(out, query), {"G-B-1"})

    def _fake(self, d, body):
        f = Path(d) / "fake_vp.py"
        f.write_text(body, encoding="utf-8")
        return [sys.executable, str(f)]

    def test_an_old_renderer_is_refused_before_the_block(self):
        with tempfile.TemporaryDirectory() as d:
            msg = pa.occlusion_query_problem(self._fake(d, self.OLD))
            self.assertIsNotNone(msg)
            self.assertIn("does not accept --occlusion-query", msg)

    def test_a_renderer_with_the_flag_passes_the_preflight(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIsNone(pa.occlusion_query_problem(self._fake(d, self.NEW)))

    def test_a_report_that_does_not_record_the_query_is_caught(self):
        with tempfile.TemporaryDirectory() as d:
            ok, bad = Path(d) / "ok-fp-planes.json", Path(d) / "bad-fp-planes.json"
            ok.write_text(json.dumps({"appearance": {"measurementMode": {"name": "depth-measurement",
                                                                          "occlusionQuery": {"conversionCheck": {}}}}}),
                          encoding="utf-8")
            bad.write_text(json.dumps({"appearance": {"measurementMode": {"name": "depth-measurement"}}}), encoding="utf-8")
            self.assertTrue(pa.report_records_query(ok))
            self.assertFalse(pa.report_records_query(bad))


if __name__ == "__main__":
    unittest.main()
