"""A raw-bytes reader of a plugin's navmesh info map (NAVI's NVMI entries) and navmeshes (NAVM's NVNM), for the navmesh
repair suites' read-backs (R7, island support). It is the SECOND READER: it shares no code with SkyWright's writer or its
Mutagen reader, and plans nothing; it only says what the bytes hold.

    from navi_bytes import Plugin
    p = Plugin("Patch.esp")
    p.navi()            # {navmesh key: Entry}, keys are (plugin file name lowercased, 24-bit id)
    p.navmeshes()       # {navmesh key: Navmesh}

Layouts (Skyrim SE, NAVI version 12, NVNM version 12), each checked as it is read: an entry or a navmesh that does not end
exactly where its subrecord ends is refused by name, so a misread layout cannot pass silently.

    NVMI  navmesh u32, unknown u32, point 3f, preferred-merges flag u32, merged-to (u32 count, u32 each), preferred merges
          (u32 count, u32 each), linked doors (u32 count, (u32 unknown, u32 door) each), island u8 (0 or 1); when 1:
          min 3f, max 3f, triangles (u32 count, 3 i16 each), vertices (u32 count, 3f each); then unknown2 u32 and the
          parent: worldspace u32 then grid x i16, y i16, or (worldspace 0) the cell u32.
    NVNM  version u32, crc u32, parent (worldspace u32 then x i16, y i16; or worldspace 0 then cell u32), vertices (u32
          count, 3f each), triangles (u32 count, 3 i16 corners, 3 i16 edges, u16 flags, u16 cover flags), edge links (u32
          count, (u32 type, u32 navmesh, i16 triangle) each), door triangles (u32 count, (i16 triangle, u32 unknown, u32
          door) each), cover triangles (u32 count, u16 each), grid divisor u32, max distance x f, y f, min 3f, max 3f, then
          the grid cells to the end.
"""
import struct
import zlib
from pathlib import Path

COMPRESSED = 0x00040000


class Entry:
    """One NVMI entry. Floats are kept as their raw 4 bytes too (bits), so a box compares bit for bit."""

    def __init__(self, raw: bytes, resolve):
        self.raw = raw
        o = 0

        def u32():
            nonlocal o
            v = struct.unpack_from("<I", raw, o)[0]
            o += 4
            return v

        def f3():
            nonlocal o
            b = raw[o:o + 12]
            o += 12
            return struct.unpack("<3f", b), b

        self.navmesh = resolve(u32())
        self.unknown = u32()
        self.point, _ = f3()
        self.preferred_flag = u32()
        self.merged_to = [resolve(u32()) for _ in range(u32())]
        self.preferred = [resolve(u32()) for _ in range(u32())]
        n = u32()
        self.doors = []
        for _ in range(n):
            unk = u32()
            self.doors.append((unk, resolve(u32())))
        flag = raw[o]
        o += 1
        if flag not in (0, 1):
            raise ValueError(f"an NVMI entry's island byte is {flag}, neither 0 nor 1, so the layout is misread")
        self.island_start = o
        self.island = None
        if flag == 1:
            (mn, mnb), (mx, mxb) = f3(), f3()
            nt = u32()
            tris = [struct.unpack_from("<3h", raw, o + 6 * i) for i in range(nt)]
            o += 6 * nt
            nv = u32()
            verts = [struct.unpack_from("<3f", raw, o + 12 * i) for i in range(nv)]
            vbits = [raw[o + 12 * i:o + 12 * i + 12] for i in range(nv)]
            o += 12 * nv
            self.island = {"min": mn, "max": mx, "min_bits": mnb, "max_bits": mxb, "triangles": tris, "vertices": verts,
                           "vertex_bits": vbits}
        self.island_end = o
        self.unknown2 = u32()
        ws = u32()
        if ws != 0:
            x, y = struct.unpack_from("<2h", raw, o)
            o += 4
            self.parent = ("world", resolve(ws), x, y)
        else:
            self.parent = ("cell", resolve(u32()))
        if o != len(raw):
            raise ValueError(f"an NVMI entry of {self.navmesh} ends {len(raw) - o} bytes before its subrecord does, so the layout is misread")

    @property
    def island_bytes(self) -> bytes:
        """The island block exactly as stored (empty for a plain entry)."""
        return self.raw[self.island_start:self.island_end]


class Navmesh:
    """One NAVM's NVNM: its vertices and its box (min, max, also as raw bits)."""

    def __init__(self, raw: bytes, resolve, key):
        o = 0

        def take(fmt):
            nonlocal o
            v = struct.unpack_from(fmt, raw, o)
            o += struct.calcsize(fmt)
            return v

        self.key = key
        self.version, self.crc = take("<2I")
        ws = take("<I")[0]
        if ws != 0:
            take("<2h")
        else:
            take("<I")
        nv = take("<I")[0]
        self.vertices = [take("<3f") for _ in range(nv)]
        nt = take("<I")[0]
        self.triangles = []
        for _ in range(nt):
            a, b, c, e0, e1, e2, flags, cover = take("<6h2H")
            self.triangles.append((a, b, c, e0, e1, e2, flags, cover))
        self.links = [take("<IIh") for _ in range(take("<I")[0])]
        self.links = [(t, resolve(m), tri) for t, m, tri in self.links]
        self.doors = [take("<hII") for _ in range(take("<I")[0])]
        self.cover = [take("<H")[0] for _ in range(take("<I")[0])]
        self.divisor, self.max_x, self.max_y = take("<I2f")
        self.min_bits, self.max_bits = raw[o:o + 12], raw[o + 12:o + 24]
        self.min, self.max = take("<3f"), take("<3f")
        cells = self.divisor * self.divisor
        for _ in range(cells):
            n = take("<I")[0]
            o += 2 * n
        if o != len(raw):
            raise ValueError(f"the NVNM of {key} ends {len(raw) - o} bytes before its subrecord does, so the layout is misread")


class Plugin:
    """One plugin file's NAVI entries and NAVM records, read straight from its bytes."""

    def __init__(self, path):
        self.path = Path(path)
        self.name = self.path.name.lower()
        self.b = self.path.read_bytes()
        if self.b[:4] != b"TES4":
            raise ValueError(f"{self.path} is not a plugin (no TES4 header)")
        size = struct.unpack_from("<I", self.b, 4)[0]
        self.masters = []
        for sig, data in self._subrecords(self.b[24:24 + size]):
            if sig == b"MAST":
                self.masters.append(data.rstrip(b"\0").decode("latin-1").lower())
        self.top = 24 + size

    def resolve(self, fid: int):
        """A FormID as (owning plugin's file name lowercased, 24-bit id); (None, 0) for a null id."""
        if fid == 0:
            return (None, 0)
        hi = fid >> 24
        owner = self.masters[hi] if hi < len(self.masters) else self.name
        return (owner, fid & 0xFFFFFF)

    @staticmethod
    def _subrecords(data: bytes):
        o, big = 0, None
        while o < len(data):
            sig, ln = data[o:o + 4], struct.unpack_from("<H", data, o + 4)[0]
            o += 6
            if sig == b"XXXX":
                big = struct.unpack_from("<I", data, o)[0]
                o += ln
                continue
            if big is not None:
                ln, big = big, None
            yield sig, data[o:o + ln]
            o += ln

    def _records(self, start=None, end=None):
        """Every record (type, flags, formid, data), walking every group."""
        o = self.top if start is None else start
        end = len(self.b) if end is None else end
        while o < end:
            sig = self.b[o:o + 4]
            size = struct.unpack_from("<I", self.b, o + 4)[0]
            if sig == b"GRUP":
                yield from self._records(o + 24, o + size)
                o += size
                continue
            flags, fid = struct.unpack_from("<II", self.b, o + 8)
            data = self.b[o + 24:o + 24 + size]
            if flags & COMPRESSED:
                data = zlib.decompress(data[4:])
            yield sig, flags, fid, data
            o += 24 + size

    def navi(self):
        """{navmesh key: Entry} of this plugin's NAVI (in its order), or None when it carries no NAVI."""
        for sig, _, _, data in self._records():
            if sig == b"NAVI":
                out = {}
                for s, d in self._subrecords(data):
                    if s == b"NVMI":
                        e = Entry(d, self.resolve)
                        out[e.navmesh] = e
                return out
        return None

    def navmeshes(self):
        """{navmesh key: Navmesh} for every NAVM with an NVNM (a deleted one has none)."""
        out = {}
        for sig, flags, fid, data in self._records():
            if sig != b"NAVM" or flags & 0x20:
                continue
            key = self.resolve(fid)
            for s, d in self._subrecords(data):
                if s == b"NVNM":
                    out[key] = Navmesh(d, self.resolve, key)
        return out


# ---- navmesh link tables (R10: carry a set of navmeshes) ----
# Each NVNM triangle's edge field is switched by its flag bit (1 << edge): set, the field indexes the navmesh's own link table
# (type, navmesh, triangle). A border is TWO-WAY when the far triangle the link names has an edge lying along the source edge
# whose own link names the source navmesh back. "Along" is measured here, apart from the writer: the far edge's projection on
# the source edge overlaps at least half the shorter of the two, and over that overlap it stays within 8 u in plan and 63 u in
# height of the source edge. Ledge links (types 1 and 2) are not made by edge geometry and are not judged.

LEDGE_TYPES = (1, 2)


def _edge(t, e):
    return (t[0], t[1]) if e == 0 else (t[1], t[2]) if e == 1 else (t[2], t[0])


def along(a, b, c, d, plan=8.0, height=63.0):
    """True when far edge c-d lies along source edge a-b (points are (x, y, z))."""
    import math
    dx, dy = b[0] - a[0], b[1] - a[1]
    ln = math.hypot(dx, dy)
    if ln < 1e-6:
        return False
    ux, uy = dx / ln, dy / ln
    tc = (c[0] - a[0]) * ux + (c[1] - a[1]) * uy
    td = (d[0] - a[0]) * ux + (d[1] - a[1]) * uy
    lo, hi = max(0.0, min(tc, td)), min(ln, max(tc, td))
    short = min(ln, math.hypot(d[0] - c[0], d[1] - c[1]))
    if hi <= lo or short < 1e-3 or (hi - lo) / short < 0.5:
        return False
    for s in (lo, hi):
        k = (s - tc) / (td - tc) if abs(td - tc) > 1e-9 else 0.0
        f = (c[0] + (d[0] - c[0]) * k, c[1] + (d[1] - c[1]) * k, c[2] + (d[2] - c[2]) * k)
        g = (a[0] + ux * s, a[1] + uy * s, a[2] + (b[2] - a[2]) * s / ln)
        if math.hypot(f[0] - g[0], f[1] - g[1]) > plan or abs(f[2] - g[2]) > height:
            return False
    return True


def border_links(nav):
    """Every cross-navmesh link of a Navmesh: (triangle, edge, (a, b) the edge's corners, type, target key, target triangle);
    type, target and triangle are None when the field names an entry past the table."""
    out = []
    for i, t in enumerate(nav.triangles):
        for e in range(3):
            if t[6] & (1 << e):
                f = t[3 + e]
                p, q = _edge(t, e)
                typ, tgt, tt = nav.links[f] if 0 <= f < len(nav.links) else (None, None, None)
                out.append((i, e, (nav.vertices[p], nav.vertices[q]), typ, tgt, tt))
    return out


def not_returned(taken, sources):
    """Each non-ledge border link of every navmesh in sources (keys of taken, {key: Navmesh} as the game takes them) that the
    navmesh it names does not return, as one line each; empty when every such border is two-way."""
    bad = []
    for k in sources:
        nav = taken[k]
        for i, e, (a, b), typ, tgt, tt in border_links(nav):
            if typ is None:
                bad.append(f"{k} triangle {i} edge {e} names a link entry past its table")
                continue
            if typ in LEDGE_TYPES or tgt == k:
                continue
            far = taken.get(tgt)
            if far is None or not 0 <= tt < len(far.triangles):
                bad.append(f"{k} triangle {i} links into {tgt} triangle {tt}, which the order does not have")
                continue
            ft = far.triangles[tt]
            back = any(ft[6] & (1 << f) and 0 <= ft[3 + f] < len(far.links) and far.links[ft[3 + f]][1] == k
                       and along(a, b, far.vertices[_edge(ft, f)[0]], far.vertices[_edge(ft, f)[1]]) for f in range(3))
            if not back:
                bad.append(f"{k} triangle {i} links into {tgt} triangle {tt}, which does not link back")
    return bad
