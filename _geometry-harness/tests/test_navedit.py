"""Known answers for navmesh editing (C2513-C2550, C2556-C2562, C2564-C2565, C2580-C2589), on _shared/NavEdit.cs, _shared/NavCutFootprint.cs and _shared/NavMarker.cs through `navedit --selftest`.

    2513  an interior vertex moved: planned, the box kept, the grid NavGrid's for the moved mesh, ops for exactly that
          vertex, the box and the grid
    2514  a move that folds a triangle is refused
    2515  a vertex on an edge linking into another navmesh: sideways refused, height-only planned
    2516  a point repeated under two indices moves as one
    2517  a move that overlaps another triangle on the same level is refused; the same triangle 200 u up is not an overlap
    2518  a vertex on a cell border: sideways refused, height-only planned
    2519  a vertex the navmesh lacks, and an empty move list, are refused in one sentence
    2520  removing a triangle moves the last one into its slot (swap-remove): fields follow, ops write it whole first
    2521  a triangle added back joins both neighbours, both ways, by a whole-triangle Add
    2522  an added triangle wound against its neighbour, or flat, is refused
    2523  an added triangle on an edge two triangles already share is refused
    2524  a triangle with a linked edge, and one before a door, are not removed
    2525  another navmesh's links follow the moved triangle, and one into the removed triangle is cut; the door follows
    2526  a cut that leaves two navmeshes with no link between them is refused
    2527  an added triangle overlapping one on the same level is refused; 200 u up it is a bridge
    2528  bad triangle input is refused in one sentence
    2529  a navmesh's own table naming itself follows the moved triangle, or is cut
    2530  NavCut: a hole round the centre leaves the area outside it, nothing over it, no half-joined edge, NavGrid's grid
    2531  NavCut: a hole inside one triangle splits its uncut neighbours at the cut points
    2532  NavCut: a non-convex, two-corner or flat outline, an empty band, and a cut that misses are refused
    2533  NavCut: the height band picks the level; above the navmesh it cuts nothing
    2534  NavCut: a cut reaching a linked edge is refused, saying so
    2535  cut of an obstacle: a box standing on the floor cuts exactly its area
    2536  the cut grown by a margin, across the triangles it reaches
    2537  an obstacle wholly above an actor's top, or below step height, is refused, naming the band
    2538  each level is measured on itself: a box on a bridge cuts the bridge, one on the floor under it the floor
    2539  two separate rocks: two holes, and the ground between them stays
    2540  an L-shaped obstacle (one mesh) is cut as the L; the notch's ground stays
    2541  two rocks joined by a thin bar of collision: the rocks and the bar go, the ground beside the bar stays
    2542  two overlapping bodies: their union goes, the overlap included
    2543  an overhang whose underside is in the band is cut; one wholly above an actor's top is refused, and not cut beside a post
    2544  navmesh lying on a rock stays; the floor round the rock is cut
    2545  sloping navmesh: each triangle measured on its own plane; a box low enough to step onto is not cut
    2546  NavCut: two outlines meeting an edge from its two sides a hair apart: their points are welded
    2547  NavCut: an outline reaching 0.3 u past an edge cuts the triangle beyond it
    2548  NavCut: a strip too narrow to stand on goes with the hole
    2549  NavCut: an outline edge parallel to a triangle's edge, a hair from it, is taken last; the far hairline stays
    2550  a notched roof is cut as two convex parts, not merged across the dip; the ground over the dip stays
    2556  use_version: another numbering of the same places: links match again, the neighbour's links are re-pointed, CopyFrom first
    2557  use_version: a border that no longer meets its neighbour is refused, listing its edges both ways
    2558  use_version: two triangles along one edge: the one the link names stands; naming neither is refused
    2559  use_version: a version that no longer links into a neighbour is refused
    2560  use_version: other doors are refused; a gained neighbour is returned for the navmesh info map
    2561  use_version: ledge links are carried or kept in place, never matched by geometry; out of range or renumbered, refused
    2562  use_version: the winner itself plans with nothing changed
    2564  use_version: the door refusal names each door added and lacked, with its triangle, door by door
    2565  door: the triangle under the arrival marker, nearest in height; flagged Door; refusals by name
    2580  marker check: a marker in the middle of a square stands inside, 100 u from the open edge
    2581  marker check: a marker 20 u from the square's edge is refused, with a gap beyond
    2582  marker check: a marker off every navmesh is refused, naming the nearest triangle's distance
    2583  marker check: an open edge with unlinked ground 87 u lower beyond it (Moon and Star's north start) is refused
    2584  marker check: an edge linked to another navmesh is not open; the link's step is measured
    2585  marker check: a linked edge whose sides stand 80 u apart is refused; 60 u stands
    2586  marker check: a deleted neighbour leaves its edge open; a link into a navmesh not given is refused as unfollowed;
          one into a navmesh the order does not have is open
    2587  marker check: a marker 300 u above the only navmesh at its XY is refused; 20 u above stands inside
    2588  marker check: a navmesh whose record box is stale is still found (the box comes from its vertices)
    2589  marker check: a link naming a triangle the linked navmesh does not have is an open edge

The real-data check (plan and write on a lab profile, read back, byte-compared with the in-game-tested patch) is in the
2026-11-21 run folder; it needs a game and an MO2 instance, so it is not a test here.

    python tests/test_navedit.py           (HC_NR_CASES=2514,2515 to pick cases)
    HC_NAVEDIT_LAB="X:/Skyrim Conflict Lab" python tests/test_navedit.py   (adds the Moon and Star marker known answer on
                                            real data: the lab's profile NR2-V; light, reads plugins only)
"""
import os
import re
import subprocess
import sys
import traceback
from pathlib import Path

HARNESS = Path(__file__).resolve().parent.parent
PROBES = Path(os.environ.get("HC_NR_HARNESS") or HARNESS)
ONLY = {s.strip() for s in (os.environ.get("HC_NR_CASES") or "").split(",") if s.strip()}
FAILURES, PASSED = [], []
SELF = None


def selftest(name):
    global SELF
    if SELF is None:
        exe = PROBES / "navedit" / "bin" / "Release" / "net9.0" / "navedit.exe"
        SELF = subprocess.run([str(exe), "--selftest"], capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=600)
    lines = [l for l in SELF.stdout.splitlines() if l.startswith((f"ok {name} ", f"FAIL {name} "))]
    assert lines and lines[0].startswith("ok "), lines or f"no line for {name} (exit {SELF.returncode}): {SELF.stdout[-400:]} {SELF.stderr[-300:]}"


# THE COPIED CORE CODE (navedit/Program.cs AddMasters, 2026-11-22). navedit re-writes a use_version patch in place to add
# the masters a resolution patch must declare; the pinned core keeps the write step internal, so its Mutagen write call and
# staging are COPIED from WriteEngine.WritePatchStaged, and the rest goes through the core's public pieces. This fingerprint
# of that code (and of those pieces' signatures) fails when a pin move changes any of it: then review AddMasters against the
# new core (or switch to the core's own extra-masters option if it has gained one), and record the new fingerprint here.
CORE = HARNESS.parent / "external" / "houseCARL" / "src" / "housecarl-core"
CORE_WRITE_FINGERPRINT = "a736548dc819b8e590a743891aba27d344e1bd07198b8c9e19dcd18110974b0f"   # pin 461681f7


def core_write_fingerprint():
    import hashlib
    import re

    def block(text, start):
        i = text.index(start)
        return text[i:text.index("\n    }\n", i) + 7]
    we = (CORE / "WriteEngine.cs").read_text(encoding="utf-8").replace("\r\n", "\n")
    parts = [block(we, "static string WritePatchStaged("), block(we, "static void CommitStagedPatch("),
             block(we, "public static void EnsureFormIdFloor(")]
    pte = (CORE / "PluginTextEncoding.cs").read_text(encoding="utf-8")
    parts += [l.strip() for l in pte.splitlines() if re.search(r"public static .* (ReadFor|WriteInPlace|InPlaceIsUtf8)\(", l)]
    parts.append(block((CORE / "AtomicFile.cs").read_text(encoding="utf-8").replace("\r\n", "\n"), "public static void Commit("))
    norm = "\n".join(re.sub(r"\s+", " ", x).strip() for x in parts)
    return hashlib.sha256(norm.encode("utf-8")).hexdigest()


MCP_COMPOSE_FINGERPRINT = "8eae05ec0081e6656222661b763582a3d2c9342ed6158167db051b18daeaef2a"   # pin 461681f7


def mcp_compose_fingerprint():
    import hashlib
    import re
    t = (HARNESS.parent / "external" / "houseCARL" / "src" / "housecarl-mcp" / "WriteMappers.cs").read_text(encoding="utf-8").replace("\r\n", "\n")

    def block(text, start):
        i = text.index(start)
        return text[i:text.index("\n    }\n", i) + 7]
    parts = [block(t, "internal static StructSpec? MapStruct("),
             t[t.index("static string[] SplitPath("):t.index("\n", t.index("=> dotted.Split"))]]
    norm = "\n".join(re.sub(r"\s+", " ", x).strip() for x in parts)
    return hashlib.sha256(norm.encode("utf-8")).hexdigest()


def mcp_compose_drift():
    got = mcp_compose_fingerprint()
    assert got == MCP_COMPOSE_FINGERPRINT, (
        f"the pinned server's compose mapping changed (fingerprint {got}, recorded {MCP_COMPOSE_FINGERPRINT}): review "
        "navedit/Program.cs MapCompose and SplitPath against housecarl-mcp WriteMappers.MapStruct and SplitPath, then "
        "record the new fingerprint")


def core_write_drift():
    builder = (CORE / "WritePatchBuilder.cs").read_text(encoding="utf-8")
    assert not re.search(r"ExtraMasters|extraMasters|AdditionalMasters", builder), (
        "the pinned core's WritePatchBuilder now names extra masters: navedit's AddMasters may be replaced by the core's "
        "own option (the one-owner route); review it")
    got = core_write_fingerprint()
    assert got == CORE_WRITE_FINGERPRINT, (
        f"the pinned core's write step changed (fingerprint {got}, recorded {CORE_WRITE_FINGERPRINT}): review "
        "navedit/Program.cs AddMasters against WriteEngine.WritePatchStaged, CommitStagedPatch, EnsureFormIdFloor, "
        "PluginTextEncoding.ReadFor/WriteInPlace and AtomicFile.Commit, then record the new fingerprint")


LAB = Path(os.environ.get("HC_NAVEDIT_LAB") or "")


def ms_marker_known_answer():
    """Real data (only with HC_NAVEDIT_LAB=<the lab instance>, which holds profiles NR2-U and NR2-V): the Moon and Star
    north start N (177792,-48952,2250) sat on a hole edge with the rock slab beyond it (SkyWright C:/hc-runs/2026-11-21-
    navgrid/LAB-RESULT.md, addendum). No navmesh is named: each arm's own are found in Tamriel by cell. N is refused for
    the open edge with higher ground beyond; Ni, 40 u further in, is still inside the 64 u margin and refused; the south
    marker S stands inside on the arm's own navmesh, Moon and Star's own 026F59:MoonAndStar_MAS.esp in NR2-U and the base game's 079A5A in NR2-V
    (houseCARL, 2026-11-22: naming them in advance is what a layout-change arm cannot do)."""
    import json, tempfile
    tmp = Path(tempfile.mkdtemp(prefix="nm-"))
    (tmp / "m.json").write_text(json.dumps({"worldspace": "00003C:Skyrim.esm", "markers": [
        {"name": "N", "at": [177792.0, -48952.0, 2250.0]}, {"name": "Ni", "at": [177792.0, -48912.0, 2250.0]},
        {"name": "S", "at": [177792.0, -49352.0, 2430.0]}]}), encoding="utf-8")
    exe = PROBES / "navedit" / "bin" / "Release" / "net9.0" / "navedit.exe"
    for profile, south in (("NR2-U", "026F59:MoonAndStar_MAS.esp"), ("NR2-V", "079A5A:Skyrim.esm")):
        out = tmp / f"r-{profile}.json"
        p = subprocess.run([str(exe), "marker", "--markers", str(tmp / "m.json"), "--out", str(out),
                            "--instance", str(LAB), "--game", os.environ.get("HC_NAVEDIT_GAME", r"C:\Games\Steam\steamapps\common\Skyrim Special Edition"),
                            "--profile", profile], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900)
        assert p.returncode == 1, f"{profile}: exit {p.returncode} (want 1: some marker refused): {p.stdout[-400:]} {p.stderr[-300:]}"
        rows = {m["name"]: m for m in json.loads(out.read_text(encoding="utf-8"))["markers"]}
        n, ni, s = rows["N"], rows["Ni"], rows["S"]
        b = (n["openEdge"] or {}).get("beyond") or {}
        assert n["verdict"] == "refused" and n["openEdge"]["distance"] < 16 and 75 <= (b.get("step") or 0) <= 110, (profile, n)
        assert ni["verdict"] == "refused" and 16 <= ni["openEdge"]["distance"] < 64, (profile, ni)
        assert s["verdict"] == "inside" and s["standing"]["navmesh"] == south, (profile, s)


CASES = [
    ("core write drift: the copied core code is unchanged", core_write_drift),
    ("compose drift: the copied server compose mapping is unchanged", mcp_compose_drift),
    ("2513 interior move planned with its grid and ops", lambda: selftest("2513")),
    ("2514 a fold is refused", lambda: selftest("2514")),
    ("2515 a linked edge holds sideways, moves in height", lambda: selftest("2515")),
    ("2516 coincident indices move together", lambda: selftest("2516")),
    ("2517 same-level overlap refused, a bridge is not", lambda: selftest("2517")),
    ("2518 a cell-border vertex holds sideways", lambda: selftest("2518")),
    ("2519 bad input refused in one sentence", lambda: selftest("2519")),
    ("2520 swap-remove renumbers and writes the moved triangle whole", lambda: selftest("2520")),
    ("2521 an added triangle joins its neighbours", lambda: selftest("2521")),
    ("2522 winding and area refused", lambda: selftest("2522")),
    ("2523 a third triangle on an edge refused", lambda: selftest("2523")),
    ("2524 linked and door triangles stay", lambda: selftest("2524")),
    ("2525 inbound links follow or are cut", lambda: selftest("2525")),
    ("2526 the last link between navmeshes refused", lambda: selftest("2526")),
    ("2527 added overlap refused, a bridge is not", lambda: selftest("2527")),
    ("2528 bad triangle input refused", lambda: selftest("2528")),
    ("2529 self-links follow or are cut", lambda: selftest("2529")),
    ("2530 NavCut centre hole is sound", lambda: selftest("2530")),
    ("2531 NavCut splits uncut neighbours", lambda: selftest("2531")),
    ("2532 NavCut refusals", lambda: selftest("2532")),
    ("2533 NavCut height band", lambda: selftest("2533")),
    ("2534 NavCut linked edge refused", lambda: selftest("2534")),
    ("2535 obstacle box cut exactly", lambda: selftest("2535")),
    ("2536 obstacle margin", lambda: selftest("2536")),
    ("2537 obstacle out of the actor band refused", lambda: selftest("2537")),
    ("2538 obstacle measured on its own level", lambda: selftest("2538")),
    ("2539 obstacle of separate pieces", lambda: selftest("2539")),
    ("2540 L-shaped obstacle cut as the L", lambda: selftest("2540")),
    ("2541 rocks joined by a thin bar", lambda: selftest("2541")),
    ("2542 overlapping bodies cut as their union", lambda: selftest("2542")),
    ("2543 overhang in the band", lambda: selftest("2543")),
    ("2544 navmesh on a rock stays", lambda: selftest("2544")),
    ("2545 sloping navmesh, own plane", lambda: selftest("2545")),
    ("2546 NavCut welds near points", lambda: selftest("2546")),
    ("2547 NavCut shallow overlap is cut", lambda: selftest("2547")),
    ("2548 NavCut strip too narrow goes", lambda: selftest("2548")),
    ("2549 NavCut parallel edge taken last", lambda: selftest("2549")),
    ("2550 notched roof not merged across the dip", lambda: selftest("2550")),
    ("2556 use_version re-points a renumbered version", lambda: selftest("2556")),
    ("2557 use_version moved border refused", lambda: selftest("2557")),
    ("2558 use_version keeps the named of two", lambda: selftest("2558")),
    ("2559 use_version lost neighbour refused", lambda: selftest("2559")),
    ("2560 use_version doors refused, neighbour gained", lambda: selftest("2560")),
    ("2561 use_version ledge links", lambda: selftest("2561")),
    ("2562 use_version the winner itself", lambda: selftest("2562")),
    ("2564 use_version names the doors", lambda: selftest("2564")),
    ("2565 door added on the marker's triangle", lambda: selftest("2565")),
    ("2580 marker in the middle stands inside", lambda: selftest("2580")),
    ("2581 marker near the edge refused, a gap beyond", lambda: selftest("2581")),
    ("2582 marker off the navmesh refused", lambda: selftest("2582")),
    ("2583 marker on a hole edge with a ledge beyond refused", lambda: selftest("2583")),
    ("2584 a linked edge is not open", lambda: selftest("2584")),
    ("2585 a linked step over the limit refused", lambda: selftest("2585")),
    ("2586 a deleted neighbour is open, an unfollowed link refused, an absent one open", lambda: selftest("2586")),
    ("2587 a marker far above the navmesh refused", lambda: selftest("2587")),
    ("2588 stale record bounds do not hide a navmesh", lambda: selftest("2588")),
    ("2589 a link to a missing triangle is open", lambda: selftest("2589")),
] + ([("MS marker known answer on the lab (real data)", ms_marker_known_answer)] if str(LAB) not in ("", ".") else [])

if __name__ == "__main__":
    for name, fn in CASES:
        if ONLY and name.split()[0] not in ONLY:
            continue
        try:
            fn()
        except Exception:
            FAILURES.append(name)
            print(f"FAIL  {name}")
            print("      " + traceback.format_exc().strip().splitlines()[-1])
        else:
            PASSED.append(name)
            print(f"ok    {name}")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    sys.exit(1 if FAILURES else 0)
