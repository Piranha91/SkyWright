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
* door triangles (sixth review), in `navmesh_links`' answer as "doors": after the edge links, a count (uint32) and per
  door triangle the triangle before the door (int16), a value not read (uint32) and the door's FormID; and a door's
  teleport destination (XTEL), in `placements_full`' answer as "teleport".
* a door's navmesh door link (tenth review), in `placements_full`' answer as "doorLink": XNDP, the navmesh's FormID
  (uint32) and its triangle at the door (int16), then two bytes not read.

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
                subs_ = dict(_subrecords(data))
                subs_["__flags__"] = flags          # the record header's flags, for a decoder that reads them
                out[fk] = decode(subs_, fk, global_)
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
    out = {"triangles": nt, "links": [[t, global_(m), tri] for t, m, tri in raw]}
    # (sixth review) the door triangles after the edge links: each the triangle before the door (int16), a value not
    # read (uint32) and the door's FormID; a geometry that ends after its edge links has none
    o += 10 * ne
    doors = []
    if o + 4 <= len(data):
        nd = struct.unpack_from("<I", data, o)[0]
        o += 4
        if o + 10 * nd > len(data):
            raise PluginReadError(f"navmesh {fk}: {nd} door triangles do not fit its geometry")
        doors = [[global_(struct.unpack_from("<I", data, o + 10 * k + 6)[0]), struct.unpack_from("<h", data, o + 10 * k)[0]]
                 for k in range(nd)]
    out["doors"] = doors
    return out


def _cell(subs, fk, global_):
    data = subs.get("DATA")
    if not data:
        return {"value": None, "flags": []}
    v = data[0] | (data[1] << 8 if len(data) > 1 else 0)
    return {"value": v, "flags": [n for b, n in CELL_FLAGS if v & b]}


def _cell_grid(subs, fk, global_):
    """An exterior cell's grid cell (XCLC: x and y, int32 each, then flags not read); None for a cell without XCLC (an
    interior or a worldspace's persistent cell)."""
    data = subs.get("XCLC")
    if not data or len(data) < 8:
        return None
    return list(struct.unpack_from("<ii", data, 0))


def cell_grids(path, form_keys):
    """(R11/N6) {formKey: [x, y] or None}: the grid cell each named exterior cell record holds; a report says an unnamed
    exterior cell by its worldspace and this grid, never by its form key."""
    return _read(path, b"CELL", form_keys, _cell_grid)


def _placement(subs, fk, global_):
    data = subs.get("DATA")
    if not data or len(data) < 24:
        return {"position": None, "rotation": None}
    v = struct.unpack_from("<6f", data, 0)
    return {"position": [round(x, 4) for x in v[:3]], "rotation": [round(x, 4) for x in v[3:]]}


def placements(path, form_keys):
    return _read(path, PLACED_TYPES, form_keys, _placement)


def _placement_full(subs, fk, global_):
    """A placed reference's DATA position and rotation, its base (NAME), scale (XSCL, 1 where absent), enable parent
    (XESP: the parent and whether the state is opposite) and the record flags the game reads at load (initially disabled
    0x800, deleted 0x20)."""
    out = _placement(subs, fk, global_)
    name = subs.get("NAME")
    out["base"] = global_(struct.unpack_from("<I", name, 0)[0]) if name and len(name) >= 4 else None
    xs = subs.get("XSCL")
    out["scale"] = round(struct.unpack_from("<f", xs, 0)[0], 4) if xs and len(xs) >= 4 else 1.0
    xe = subs.get("XESP")
    out["enableParent"] = ([global_(struct.unpack_from("<I", xe, 0)[0]), bool(xe[4] & 1) if len(xe) > 4 else False]
                           if xe and len(xe) >= 4 else None)
    f = int(subs.get("__flags__") or 0)
    out["initiallyDisabled"], out["deleted"] = bool(f & 0x800), bool(f & 0x20)
    # (sixth review) a door's teleport (XTEL): the destination door it leads to, None where it has none
    xt = subs.get("XTEL")
    out["teleport"] = global_(struct.unpack_from("<I", xt, 0)[0]) if xt and len(xt) >= 4 else None
    # (tenth review) a door's navmesh door link (XNDP): the navmesh and its triangle at the door, None where it has none
    xn = subs.get("XNDP")
    out["doorLink"] = ([global_(struct.unpack_from("<I", xn, 0)[0]), struct.unpack_from("<h", xn, 4)[0]]
                       if xn and len(xn) >= 6 else None)
    return out


def placements_full(path, form_keys):
    """placements, with the base, scale, enable parent and load-time flags (see _placement_full)."""
    return _read(path, PLACED_TYPES, form_keys, _placement_full)


def placed_of_bases(path, base_keys):
    """(ninth review, BPT 33) Every placed reference this plugin holds a version of whose base (NAME) is one of
    `base_keys`: {"plugin", "masters", "refs": {reference formKey: base formKey}}. Read through the cell and worldspace
    groups; a compressed record is decompressed."""
    p = Path(path)
    masters, places = _index(p, PLACED_TYPES)
    want = {str(b).lower() for b in base_keys}

    def global_(fid):
        idx = fid >> 24
        return f"{fid & 0xFFFFFF:06X}:{masters[idx] if idx < len(masters) else p.name}"
    refs = {}
    try:
        with p.open("rb") as fh:
            for (typ, fid), (at, size, flags) in places.items():
                fh.seek(at)
                data = fh.read(size)
                if flags & COMPRESSED:
                    try:
                        data = zlib.decompress(data[4:])
                    except zlib.error as e:
                        raise PluginReadError(f"record {global_(fid)}: a compressed record does not decompress: {e}") from e
                name = dict(_subrecords(data)).get("NAME")
                if name and len(name) >= 4:
                    b_ = global_(struct.unpack_from("<I", name, 0)[0])
                    if b_.lower() in want:
                        refs[global_(fid)] = b_
    except OSError as e:
        raise PluginReadError(f"cannot read {p}: {e}") from e
    return {"plugin": p.name, "masters": masters, "refs": refs}


def navmesh_links(path, form_keys):
    return _read(path, b"NAVM", form_keys, _navmesh)


def cell_flags(path, form_keys):
    return _read(path, b"CELL", form_keys, _cell)


def navmesh_doors_all(path):
    """(sixth review) Every navmesh record the plugin holds, with the doors its door triangles name: {formKey: [door
    formKey, ...]} (a navmesh without geometry: []). One pass over the file's navmesh records."""
    p = Path(path)
    masters, places = _index(p, (b"NAVM", b"CELL"))
    want = [f"{fid & 0xFFFFFF:06X}:{masters[fid >> 24] if (fid >> 24) < len(masters) else p.name}"
            for (t, fid) in places if t == b"NAVM"]
    got = _read(p, b"NAVM", want, _navmesh)["records"]
    return {fk: [d for d, _ in (v or {}).get("doors") or []] for fk, v in got.items()}


def navmesh_places(path, form_keys):
    """(item 1) These navmesh records' triangle counts and places (see _navmesh_place), as navmesh_places_all reads
    them, for the records asked: {"records": {formKey: {"triangles", "place"}}}."""
    return _read(path, b"NAVM", form_keys, _navmesh_place)


def navmesh_places_all(path):
    """(tenth review) Every navmesh record the plugin holds, with its triangle count and place (see _navmesh):
    {formKey: {"triangles": n, "place": [...]}} (a navmesh without geometry: triangles None, place None)."""
    p = Path(path)
    masters, places = _index(p, (b"NAVM", b"CELL"))
    want = [f"{fid & 0xFFFFFF:06X}:{masters[fid >> 24] if (fid >> 24) < len(masters) else p.name}"
            for (t, fid) in places if t == b"NAVM"]
    got = _read(p, b"NAVM", want, _navmesh_place)["records"]
    return {fk: v or {"triangles": None, "place": None} for fk, v in got.items()}


def _navmesh_place(subs, fk, global_):
    """(tenth review) A navmesh's triangle count and where it lies, from its geometry's header: an interior cell
    (worldspace 0, then the cell's FormID) or a worldspace and its grid cell (two int16)."""
    data = subs.get("NVNM")
    if data is None or len(data) < 24:
        return {"triangles": None, "place": None}
    try:
        nv = struct.unpack_from("<I", data, 16)[0]
        nt = struct.unpack_from("<I", data, 20 + 12 * nv)[0]
    except struct.error as e:
        raise PluginReadError(f"navmesh {fk}: its geometry ends before its counts say: {e}") from e
    world_ = struct.unpack_from("<I", data, 8)[0]
    return {"triangles": nt, "place": (["cell", global_(struct.unpack_from("<I", data, 12)[0])] if world_ == 0 else
                                       ["grid", global_(world_), *struct.unpack_from("<hh", data, 12)])}


_TOP = {}


def record_flags(path, form_keys, rtype):
    """(sixth review) The record header flags of named records in one top-level group of the plugin (a base record's
    group, e.g. STAT): {formKey: flags, or None where the plugin holds no such record}. The group is indexed once per
    file state."""
    p = Path(path)
    try:
        st = p.stat()
        key = (str(p).lower(), st.st_size, st.st_mtime_ns, rtype)
        if key not in _TOP:
            buf = p.read_bytes()
            if buf[:4] != b"TES4":
                raise PluginReadError(f"{p.name} is not a plugin file (no TES4 header)")
            hsize = struct.unpack_from("<I", buf, 4)[0]
            masters = [d.rstrip(b"\x00").decode("latin-1") for s, d in _subrecords(buf[24:24 + hsize]) if s == "MAST"]
            got, i = {}, 24 + hsize
            while i + 24 <= len(buf):
                size = struct.unpack_from("<I", buf, i + 4)[0]
                if buf[i:i + 4] == b"GRUP":
                    if buf[i + 8:i + 12] == rtype:
                        j, end = i + 24, i + size
                        while j + 24 <= end:
                            rs = struct.unpack_from("<I", buf, j + 4)[0]
                            flags, fid = struct.unpack_from("<II", buf, j + 8)
                            got[fid] = flags
                            j += 24 + rs
                    i += size
                else:
                    i += 24 + size
            _TOP[key] = (masters, got)
    except (OSError, struct.error) as e:
        raise PluginReadError(f"cannot read {p}: {e}") from e
    masters, got = _TOP[key]
    low = [m.lower() for m in masters]
    out = {}
    for fk in form_keys:
        obj, _, origin = str(fk).partition(":")
        idx = low.index(origin.lower()) if origin.lower() in low else (
            len(masters) if origin.lower() == p.name.lower() else None)
        out[str(fk)] = got.get((idx << 24) | int(obj, 16)) if idx is not None else None
    return out


_EDID = {}


def editor_ids(path, form_keys):
    """(seventh review, Missives S2) The editor ID (EDID) of named records anywhere in the plugin's top-level groups
    (records nested in cell, worldspace or topic groups are not read): {formKey: editor ID, or None where the plugin holds
    no such record or it has no EDID}. The file is indexed once per file state."""
    p = Path(path)
    try:
        st = p.stat()
        key = (str(p).lower(), st.st_size, st.st_mtime_ns)
        if key not in _EDID:
            buf = p.read_bytes()
            if buf[:4] != b"TES4":
                raise PluginReadError(f"{p.name} is not a plugin file (no TES4 header)")
            hsize = struct.unpack_from("<I", buf, 4)[0]
            masters = [d.rstrip(b"\x00").decode("latin-1") for s, d in _subrecords(buf[24:24 + hsize]) if s == "MAST"]
            got, i = {}, 24 + hsize
            while i + 24 <= len(buf):
                size = struct.unpack_from("<I", buf, i + 4)[0]
                if buf[i:i + 4] != b"GRUP":
                    i += 24 + size
                    continue
                j, end = i + 24, i + size
                while j + 24 <= end:
                    rs = struct.unpack_from("<I", buf, j + 4)[0]
                    if buf[j:j + 4] == b"GRUP":
                        j += rs
                        continue
                    flags, fid = struct.unpack_from("<II", buf, j + 8)
                    got[fid] = (j + 24, rs, flags)
                    j += 24 + rs
                i += size
            _EDID[key] = (masters, got, buf)
    except (OSError, struct.error) as e:
        raise PluginReadError(f"cannot read {p}: {e}") from e
    masters, got, buf = _EDID[key]
    low = [m.lower() for m in masters]
    out = {}
    for fk in form_keys:
        obj, _, origin = str(fk).partition(":")
        idx = low.index(origin.lower()) if origin.lower() in low else (
            len(masters) if origin.lower() == p.name.lower() else None)
        place = got.get((idx << 24) | int(obj, 16)) if idx is not None and len(obj) == 6 else None
        if place is None:
            out[str(fk)] = None
            continue
        at, size, flags = place
        data = buf[at:at + size]
        if flags & COMPRESSED:
            try:
                data = zlib.decompress(data[4:])
            except zlib.error as e:
                raise PluginReadError(f"record {fk}: a compressed record does not decompress: {e}") from e
        ed = dict(_subrecords(data)).get("EDID")
        out[str(fk)] = ed.rstrip(b"\x00").decode("latin-1") if ed else None
    return out


_WORLDS = {}


def cell_worlds(path, form_keys):
    """(eighth review, Missives) The worldspace each named cell record is filed under: the world-children group (type 1)
    holding it, whose label is the worldspace's FormID: {formKey: "XXXXXX:Origin.esm", or None where the plugin holds no
    such cell or files it under no worldspace (an interior)}. Group headers only; the file is indexed once per state."""
    p = Path(path)
    try:
        st = p.stat()
        key = (str(p).lower(), st.st_size, st.st_mtime_ns)
        if key not in _WORLDS:
            buf = p.read_bytes()
            if buf[:4] != b"TES4":
                raise PluginReadError(f"{p.name} is not a plugin file (no TES4 header)")
            hsize = struct.unpack_from("<I", buf, 4)[0]
            masters = [d.rstrip(b"\x00").decode("latin-1") for s_, d in _subrecords(buf[24:24 + hsize]) if s_ == "MAST"]
            got = {}

            def walk(i, end, world):
                while i + 24 <= end:
                    size = struct.unpack_from("<I", buf, i + 4)[0]
                    if buf[i:i + 4] == b"GRUP":
                        if size < 24 or i + size > end:
                            raise PluginReadError("a group whose size does not fit the file")
                        gtype = struct.unpack_from("<I", buf, i + 12)[0]
                        walk(i + 24, i + size, struct.unpack_from("<I", buf, i + 8)[0] if gtype == 1 else world)
                        i += size
                        continue
                    if buf[i:i + 4] == b"CELL" and world is not None:
                        got[struct.unpack_from("<I", buf, i + 12)[0]] = world
                    i += 24 + size
            walk(24 + hsize, len(buf), None)
            _WORLDS[key] = (masters, got)
    except (OSError, struct.error) as e:
        raise PluginReadError(f"cannot read {p}: {e}") from e
    masters, got = _WORLDS[key]
    low = [m.lower() for m in masters]

    def global_(fid):
        idx = fid >> 24
        return f"{fid & 0xFFFFFF:06X}:{masters[idx] if idx < len(masters) else p.name}"
    out = {}
    for fk in form_keys:
        obj, _, origin = str(fk).partition(":")
        idx = low.index(origin.lower()) if origin.lower() in low else (
            len(masters) if origin.lower() == p.name.lower() else None)
        w_ = got.get((idx << 24) | int(obj, 16)) if idx is not None and len(obj) == 6 else None
        out[str(fk)] = global_(w_) if w_ is not None else None
    return out
