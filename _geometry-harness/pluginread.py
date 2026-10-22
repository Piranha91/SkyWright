"""Read-only reads of record data the record imports do not carry, from one plugin file (third review, round 2).

Two reads, each of named records only:

* navmesh edge links (`navmesh_links`): a navmesh record's geometry (NVNM) holds, after its vertices and triangles, its
  edge links: the triangles of other navmeshes its own triangles connect to. Each entry is a type (uint32), the linked
  navmesh's FormID and the linked triangle (int16). The layout read:
      version u32, magic u32, worldspace u32, cell or grid u32,
      vertex count u32, vertices 12 bytes each, triangle count u32, triangles 16 bytes each,
      edge-link count u32, edge links 10 bytes each, ... (door triangles, cover, grid: not read)
* cell flags (`cell_flags`): a cell record's DATA subrecord, a uint16 of flags (one byte in some records), named by
  the bits the game defines (CELL_FLAGS).
* placements (`placements`, fourth review): a placed reference's DATA subrecord, its position and rotation (six
  float32: x, y, z, then rotation x, y, z in radians), for the placed record types (PLACED_TYPES).

A FormID is read as this plugin writes it: the high byte is the index of the record's origin file in the plugin's master
list, or the master count for the plugin's own records; every answer names records as "XXXXXX:Origin.esm".

    navmesh_links(path, ["04C167:Skyrim.esm"]) -> {"plugin": name, "masters": [...],
        "records": {formKey: {"triangles": n, "links": [[type, "10C67D:Skyrim.esm", 471], ...]} or None}}
    cell_flags(path, ["03A183:Skyrim.esm"]) -> {"plugin": name, "masters": [...],
        "records": {formKey: {"value": 129, "flags": ["IsInteriorCell", "ShowSky"]} or None}}

None for a record the plugin does not hold; a navmesh record without NVNM (a deletion) has "triangles": None and no
links; a cell record without DATA has "value": None. Raises PluginReadError when the file cannot be read as a plugin or a
record does not fit its own counts; the caller then treats the read as unavailable.
"""
import struct
import zlib
from pathlib import Path

COMPRESSED = 0x00040000
CELL_FLAGS = [(0x0001, "IsInteriorCell"), (0x0002, "HasWater"), (0x0004, "CantTravelFromHere"), (0x0008, "NoLodWater"),
              (0x0020, "PublicArea"), (0x0040, "HandChanged"), (0x0080, "ShowSky"), (0x0100, "UseSkyLighting")]


PLACED_TYPES = (b"REFR", b"ACHR", b"PGRE", b"PMIS", b"PHZD", b"PARW", b"PBAR", b"PBEA", b"PCON", b"PFLA")


class PluginReadError(Exception):
    pass


def _subrecords(data):
    """(signature, payload) of each subrecord; an XXXX subrecord gives the size of the next one."""
    i, big = 0, None
    while i + 6 <= len(data):
        sig = data[i:i + 4]
        size = struct.unpack_from("<H", data, i + 4)[0]
        i += 6
        if sig == b"XXXX" and size == 4:
            big = struct.unpack_from("<I", data, i)[0]
            i += 4
            continue
        if big is not None:
            size, big = big, None
        yield sig.decode("latin-1"), data[i:i + size]
        i += size


_INDEX = {}


def _offsets(buf, start, end, out, types=(b"NAVM", b"CELL")):
    """(type, FormID) -> (offset, size, flags) of every record of `types` (navmesh and cell records by default), through
    the cell and worldspace groups."""
    i = start
    while i + 24 <= end:
        typ = buf[i:i + 4]
        size = struct.unpack_from("<I", buf, i + 4)[0]
        if typ == b"GRUP":
            if size < 24 or i + size > end:
                raise PluginReadError("a group whose size does not fit the file")
            gtype = struct.unpack_from("<I", buf, i + 12)[0]
            if gtype != 0 or buf[i + 8:i + 12] in (b"CELL", b"WRLD"):
                _offsets(buf, i + 24, i + size, out, types)
            i += size
            continue
        if typ in types:
            flags, fid = struct.unpack_from("<II", buf, i + 8)
            if i + 24 + size > end:
                raise PluginReadError(f"a {typ.decode()} record runs past the end of its group")
            out[(typ, fid)] = (i + 24, size, flags)
        i += 24 + size


def _index(p, types=(b"NAVM", b"CELL")):
    """The plugin's masters and its navmesh and cell records' places, built once per file state (size, modified time);
    the file is read once and no handle is kept."""
    try:
        st = p.stat()
    except OSError as e:
        raise PluginReadError(f"cannot read {p}: {e}") from e
    key = (str(p).lower(), st.st_size, st.st_mtime_ns, tuple(types))
    if key not in _INDEX:
        try:
            buf = p.read_bytes()
        except OSError as e:
            raise PluginReadError(f"cannot read {p}: {e}") from e
        if buf[:4] != b"TES4":
            raise PluginReadError(f"{p.name} is not a plugin file (no TES4 header)")
        hsize = struct.unpack_from("<I", buf, 4)[0]
        masters = [d.rstrip(b"\x00").decode("latin-1") for s, d in _subrecords(buf[24:24 + hsize]) if s == "MAST"]
        places = {}
        _offsets(buf, 24 + hsize, len(buf), places, tuple(types))
        _INDEX[key] = (masters, places)
    return _INDEX[key]


def _read(path, rtype, form_keys, decode):
    p = Path(path)
    rtypes = rtype if isinstance(rtype, tuple) else (rtype,)
    masters, places = _index(p, rtypes if rtypes[0] in PLACED_TYPES else (b"NAVM", b"CELL"))
    low = [m.lower() for m in masters]

    def global_(fid):
        idx = fid >> 24
        return f"{fid & 0xFFFFFF:06X}:{masters[idx] if idx < len(masters) else p.name}"

    out = {str(fk): None for fk in form_keys}
    want = {}
    for fk in form_keys:
        obj, _, origin = str(fk).partition(":")
        idx = low.index(origin.lower()) if origin.lower() in low else (
            len(masters) if origin.lower() == p.name.lower() else None)
        if idx is not None and len(obj) == 6 and all(c in "0123456789abcdefABCDEF" for c in obj):
            fid = (idx << 24) | int(obj, 16)
            for rt_ in rtypes:
                if (rt_, fid) in places:
                    want[str(fk)] = places[(rt_, fid)]
                    break
    if not want:
        return {"plugin": p.name, "masters": masters, "records": out}
    try:
        with p.open("rb") as fh:
            for fk, (at, size, flags) in want.items():
                fh.seek(at)
                data = fh.read(size)
                if flags & COMPRESSED:
                    try:
                        data = zlib.decompress(data[4:])
                    except zlib.error as e:
                        raise PluginReadError(f"record {fk}: a compressed record does not decompress: {e}") from e
                out[fk] = decode(dict(_subrecords(data)), fk, global_)
    except OSError as e:
        raise PluginReadError(f"cannot read {p}: {e}") from e
    return {"plugin": p.name, "masters": masters, "records": out}


def _navmesh(subs, fk, global_):
    data = subs.get("NVNM")
    if data is None:
        return {"triangles": None, "links": []}
    try:
        o = 16
        nv = struct.unpack_from("<I", data, o)[0]
        o += 4 + 12 * nv
        nt = struct.unpack_from("<I", data, o)[0]
        o += 4 + 16 * nt
        ne = struct.unpack_from("<I", data, o)[0]
        o += 4
    except struct.error as e:
        raise PluginReadError(f"navmesh {fk}: its geometry ends before its counts say: {e}") from e
    if o + 10 * ne > len(data):
        raise PluginReadError(f"navmesh {fk}: {ne} edge links do not fit its geometry")
    raw = [struct.unpack_from("<IIh", data, o + 10 * k) for k in range(ne)]
    return {"triangles": nt, "links": [[t, global_(m), tri] for t, m, tri in raw]}


def _cell(subs, fk, global_):
    data = subs.get("DATA")
    if not data:
        return {"value": None, "flags": []}
    v = data[0] | (data[1] << 8 if len(data) > 1 else 0)
    return {"value": v, "flags": [n for b, n in CELL_FLAGS if v & b]}


def _placement(subs, fk, global_):
    data = subs.get("DATA")
    if not data or len(data) < 24:
        return {"position": None, "rotation": None}
    v = struct.unpack_from("<6f", data, 0)
    return {"position": [round(x, 4) for x in v[:3]], "rotation": [round(x, 4) for x in v[3:]]}


def placements(path, form_keys):
    return _read(path, PLACED_TYPES, form_keys, _placement)


def navmesh_links(path, form_keys):
    return _read(path, b"NAVM", form_keys, _navmesh)


def cell_flags(path, form_keys):
    return _read(path, b"CELL", form_keys, _cell)
