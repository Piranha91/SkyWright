"""evalbase - what the evaluation tools on both sides of the houseCARL integration fence share.

A library with no entry point. Bundle enrichment (evalrun enrich, bundleenrich), and in the module the record capture
(evalrecords) and the post-bundle pipeline (evalpipeline), all use these, and the pipeline imports them from here, so
`evalpipeline.Refusal is evalbase.Refusal` and one `except` catches both:

  Refusal             one sentence: what went wrong and what to try
  plugin_header       a plugin's TES4 header (masters, ESM/ESL flags) and the file's sha256
  plugin_records      the record count a header states
  runcontrol_exe      the built-in controller: the machine file's, else HOUSECARL_RUNCONTROL, else this checkout's build
  verify_bundle_argv  the bundle check, `runcontrol verify-bundle`, its one owner
  arm_masters         each plugin's masters, closed transitively, and which closures meet an unreadable header
"""
from __future__ import annotations

import hashlib
import os
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))
import buildlayout  # noqa: E402  where the build put runcontrol


class Refusal(Exception):
    """One sentence: what went wrong and what to try."""


def sha256_file(p) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def fwd(p) -> str:
    """A path in forward-slash spelling: argument parsers downstream have lost backslashes before."""
    return str(p).replace("\\", "/")


# ---------------------------------------------------------------- plugin headers

def _header_fields(p: Path) -> tuple[int, list[str], int | None]:
    """(flags, masters, HEDR record count) from one plugin's TES4 header. Refuses a file that is not a plugin."""
    with open(p, "rb") as f:
        head = f.read(24)
        if len(head) < 24 or head[:4] != b"TES4":
            raise Refusal(f"{p.name} is not a plugin (no TES4 header); check the file at {fwd(p)}.")
        size, flags = struct.unpack_from("<II", head, 4)
        body = f.read(size)
    masters, records, i, big = [], None, 0, None
    while i + 6 <= len(body):
        sig = body[i:i + 4]
        n = struct.unpack_from("<H", body, i + 4)[0]
        if big is not None:
            n, big = big, None
        data = body[i + 6:i + 6 + n]
        if sig == b"XXXX":
            big = struct.unpack_from("<I", data, 0)[0]
        elif sig == b"MAST":
            masters.append(data.split(b"\0", 1)[0].decode("cp1252"))
        elif sig == b"HEDR" and len(data) >= 8:
            records = struct.unpack_from("<I", data, 4)[0]
        i += 6 + n
    return flags, masters, records


def plugin_header(path) -> dict:
    """The TES4 header of one plugin: masters, ESM/ESL flags and the file's sha256. Refuses a file that is not a plugin."""
    p = Path(path)
    flags, masters, _ = _header_fields(p)
    return {"path": fwd(p), "sha256": sha256_file(p), "masters": masters, "esm": bool(flags & 0x1),
            "esl": bool(flags & 0x200)}


def plugin_records(path) -> int | None:
    """The record count a plugin's TES4 header states (HEDR); None when it states none."""
    return _header_fields(Path(path))[2]


# ---------------------------------------------------------------- the built-in controller

#: The environment variable naming runcontrol.exe for a tool started without a machine file (evalreport, evalrun enrich);
#: the pipeline sets it on every child from the machine file's `runcontrol`.
RUNCONTROL_ENV = "HOUSECARL_RUNCONTROL"


def runcontrol_exe(machine: dict | None) -> Path:
    """The built-in controller: the machine file's `runcontrol`, else HOUSECARL_RUNCONTROL, else this checkout's build;
    refused when it is absent."""
    named = (machine or {}).get("runcontrol") or (os.environ.get(RUNCONTROL_ENV) or "").strip()
    exe = Path(named) if named else buildlayout.runcontrol()
    if not exe.is_file():
        raise Refusal(f"the built-in controller is not at {fwd(exe)}; build it (scripts/build.ps1) or "
                      f"name runcontrol.exe under the key runcontrol in the machine file.")
    return exe


def verify_bundle_argv(machine: dict | None, bundle: Path) -> list[str]:
    """The bundle check: the built-in controller's `verify-bundle`, its one owner."""
    return [fwd(runcontrol_exe(machine)), "verify-bundle", fwd(bundle)]


def arm_masters(arm: dict, recorded: dict | None = None):
    """(masters_of, masters_unread) for one arm: each plugin's masters, transitively, from the TES4 header of the file the arm
    loads (the master list the engine resolves the plugin's records against), named without case; and whether a plugin's
    closure meets a header that could not be read (no file for it in the arm, a file that is gone or not a plugin).
    `recorded` ({plugin casefold: [masters]}): master lists a measurement recorded for these very bytes, used before the
    file's header, so a file changed after the measurement does not blind its dependents."""
    files = {str(p.get("name")).casefold(): p.get("path") for p in (arm.get("requested") or {}).get("plugins") or []}
    direct = {}
    for name in arm["plugins"]:
        if recorded and name.casefold() in recorded:
            direct[name.casefold()] = [str(m).casefold() for m in recorded[name.casefold()]]
            continue
        path = files.get(name.casefold())
        try:
            direct[name.casefold()] = [str(m).casefold() for m in plugin_header(path)["masters"]] if path else None
        except (OSError, ValueError, struct.error, Refusal):
            direct[name.casefold()] = None
    closure = {}
    for name in direct:
        seen, blind, stack = set(), direct[name] is None, list(direct[name] or [])
        while stack:
            m = stack.pop()
            if m in seen:
                continue
            seen.add(m)
            if direct.get(m) is None:
                blind = True
            else:
                stack.extend(direct[m])
        closure[name] = (seen, blind)
    return (lambda p: closure.get(p.casefold(), (set(), True))[0]), (lambda p: closure.get(p.casefold(), (set(), True))[1])
