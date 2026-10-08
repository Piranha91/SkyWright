"""Known answers for planeassess's records steps: edit kinds, record classes, the shape difference and the
geometry self-test. python -B -m pytest scripts/planeassess/test_planeassess.py (or run it directly)."""
import copy
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


if __name__ == "__main__":
    unittest.main()
