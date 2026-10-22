"""Read-only scan of one plugin file for records that refer to given objects (third review, Pilgrim S1).

A reference is the object's FormID, as this plugin writes it (the high byte is the index of the object's origin file in
the plugin's own master list, or the master count for the plugin's own objects), found anywhere in a record's data:
every subrecord, script properties (VMAD) included, compressed records decompressed. The object's own record is not a
reference to it. A match anywhere is reported; a chance match of four bytes only errs toward "referred to".

    scan(path, ["09BDC3:Skyrim.esm"]) -> {"plugin": name, "masters": [...], "refs": {formKey: [{type, formId, sub}]}}

Raises RefScanError when the file cannot be read as a plugin; the caller then treats the read as unavailable.
"""
import struct
import zlib
from pathlib import Path

COMPRESSED = 0x00040000


class RefScanError(Exception):
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


def _records(buf, start, end):
    """(type, flags, formId, data) of each record between start and end, through groups at any depth."""
    i = start
    while i + 24 <= end:
        typ = buf[i:i + 4]
        size = struct.unpack_from("<I", buf, i + 4)[0]
        if typ == b"GRUP":
            if size < 24:
                raise RefScanError("a group with a size smaller than its header")
            yield from _records(buf, i + 24, i + size)
            i += size
            continue
        flags, fid = struct.unpack_from("<II", buf, i + 8)
        data = buf[i + 24:i + 24 + size]
        if len(data) != size:
            raise RefScanError("a record runs past the end of the file")
        if flags & COMPRESSED:
            if size < 4:
                raise RefScanError("a compressed record without its size")
            try:
                data = zlib.decompress(data[4:])
            except zlib.error as e:
                raise RefScanError(f"a compressed record does not decompress: {e}") from e
        yield typ.decode("latin-1"), flags, fid, data
        i += 24 + size


def scan(path, form_keys):
    p = Path(path)
    try:
        buf = p.read_bytes()
    except OSError as e:
        raise RefScanError(f"cannot read {p}: {e}") from e
    if buf[:4] != b"TES4":
        raise RefScanError(f"{p.name} is not a plugin file (no TES4 header)")
    hsize = struct.unpack_from("<I", buf, 4)[0]
    masters = [d.rstrip(b"\x00").decode("latin-1") for s, d in _subrecords(buf[24:24 + hsize]) if s == "MAST"]
    low = [m.lower() for m in masters]
    wanted = {}
    for fk in form_keys:
        obj, _, origin = str(fk).partition(":")
        idx = low.index(origin.lower()) if origin.lower() in low else (len(masters) if origin.lower() == p.name.lower() else None)
        if idx is None:
            continue                            # the object's file is not among this plugin's masters: it cannot refer to it
        wanted[struct.pack("<I", (idx << 24) | (int(obj, 16) & 0xFFFFFF))] = str(fk)
    refs = {fk: [] for fk in form_keys}
    for typ, _, fid, data in _records(buf, 24 + hsize, len(buf)):
        own = struct.pack("<I", fid)
        for sig, payload in _subrecords(data):
            for needle, fk in wanted.items():
                if needle != own and needle in payload:
                    refs[fk].append({"type": typ, "formId": f"{fid:08X}", "sub": sig})
    return {"plugin": p.name, "masters": masters, "refs": refs}
