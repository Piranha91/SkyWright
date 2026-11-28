"""The vanilla floor of the island rules (R7, Gate A review condition 1), measured on the base game's five files by the
raw-bytes reader (navi_bytes.py), entry by entry:

    box rule   an island entry's box (min, max) is, bit for bit, the box its navmesh stores (NVNM), the navmesh as the
               latest of these files up to and including the entry's own file has it;
    flag rule  an entry's first Unknown is 32 exactly when the entry holds island data.

    python _geometry-harness/tests/navi_floor.py [DATA_DIR]   (prints one line per file and the totals as JSON)

test_navedit.py's real-data case calls floor() and holds its counts; it is skipped by name when the game's Data is absent.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from navi_bytes import Plugin  # noqa: E402

FILES = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"]
DEFAULT_DATA = Path("C:/Games/Steam/steamapps/common/Skyrim Special Edition/Data")


def floor(data: Path):
    """Per file: entries, islands, empty-mesh islands, island boxes equal to the navmesh's (and the misses, by key), island
    entries whose navmesh none of the files has, the flag rule's agreements and each way it can fail."""
    navs = {}
    rows = []
    for name in FILES:
        p = Plugin(data / name)
        navs.update(p.navmeshes())
        navi = p.navi() or {}
        r = {"file": name, "entries": len(navi), "islands": 0, "emptyMesh": 0, "boxExact": 0, "boxMiss": [], "noNavmesh": 0,
             "flagAgrees": 0, "islandWithout32": [], "flag32WithoutIsland": [], "unknownValues": {}}
        for key, e in navi.items():
            r["unknownValues"][str(e.unknown)] = r["unknownValues"].get(str(e.unknown), 0) + 1
            isl = e.island is not None
            if (e.unknown == 32) == isl:
                r["flagAgrees"] += 1
            elif isl:
                r["islandWithout32"].append(f"{key[1]:06X}:{key[0]} ({e.unknown})")
            else:
                r["flag32WithoutIsland"].append(f"{key[1]:06X}:{key[0]}")
            if not isl:
                continue
            r["islands"] += 1
            if not e.island["vertices"] and not e.island["triangles"]:
                r["emptyMesh"] += 1
            n = navs.get(key)
            if n is None:
                r["noNavmesh"] += 1
            elif n.min_bits == e.island["min_bits"] and n.max_bits == e.island["max_bits"]:
                r["boxExact"] += 1
            else:
                r["boxMiss"].append(f"{key[1]:06X}:{key[0]}")
        rows.append(r)
    return rows


if __name__ == "__main__":
    d = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_DATA
    for r in floor(d):
        print(json.dumps({k: (v[:10] if isinstance(v, list) else v) for k, v in r.items()}))
