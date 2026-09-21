"""How many renders may run side by side on this machine, and why that many.

A driver that starts n renders because n was typed is guessing. Four campaign-shaped renders at
once have been measured leaving graphics and system memory nearly exhausted TOGETHER, so the limit
is computed from what is free at launch, bounded by both, and from what ONE process of THIS job
needs - measured from a solo run of the same job, or supplied by the caller. There is deliberately
no constant here for what a render costs: the cost depends on the cell, the size and the report,
and a number baked into this file would be wrong on the next job and would never say so.

When the machine cannot be asked - no reader on PATH, a reader that fails, a reader that answers
something that is not a number - the answer is ONE process with the reason, or a refusal. Never
"as many as asked": a limit that silently becomes the request is the failure this module exists to
prevent.

Used by `renderagree.py`; imported rather than copied by any driver that batches renders.

    from renderlimit import Need, plan_limit, read_graphics_free, read_system_free
    plan = plan_limit(asked=4, need=Need(3000.0, 4000.0, "measured"),
                      graphics=read_graphics_free(), system=read_system_free())
    print(plan.line())
"""
from __future__ import annotations

import ctypes
import shutil
import subprocess
import time
from dataclasses import dataclass, field

GRAPHICS = "graphics"
SYSTEM = "system"
UNREADABLE = "unreadable"
ASKED = "asked"

# POLICY, not measurement: memory held back so the machine is still usable and so a render that
# grows a little past its measured need does not take the others down with it. Overridable per
# call (`reserve_mib=`), and printed in the plan line so nobody has to read this file to find it.
RESERVE_MIB = 2000.0

NVIDIA_SMI = "nvidia-smi"


@dataclass
class Reading:
    """What a memory reader answered, including the case where it did not answer."""
    resource: str
    free_mib: float | None
    total_mib: float | None
    source: str
    ok: bool
    reason: str = ""

    def describe(self) -> str:
        if self.ok:
            return "%s memory: %.0f MiB free of %.0f (%s)" % (
                self.resource, self.free_mib or 0.0, self.total_mib or 0.0, self.source)
        return "%s memory could not be read (%s)" % (self.resource, self.reason)


@dataclass
class Need:
    """What ONE process of this job needs. `source` says how it is known - "measured" from a solo
    run of the same job, or "supplied" by the caller - because the two deserve different trust."""
    graphics_mib: float
    system_mib: float
    source: str = "supplied"


@dataclass
class Plan:
    limit: int
    refused: bool
    basis: str
    reason: str
    detail: dict = field(default_factory=dict)

    def line(self) -> str:
        return "side-by-side limit: %d  (%s; %s)" % (self.limit, self.basis, self.reason)


# ------------------------------------------------------------------------- readers --

def read_graphics_free(run=subprocess.run, which=shutil.which) -> Reading:
    """Graphics memory, from nvidia-smi. `run` and `which` are injectable so the failure cases can
    be decided by a test rather than by the hardware that happens to run it.

    Anything unexpected is a NON-answer carrying what came back, never a number: a reader that
    answers "N/A" has not answered, and parsing that as free memory is exactly the silent wrong
    answer the caller must not be given.
    """
    exe = which(NVIDIA_SMI)
    if not exe:
        return Reading(GRAPHICS, None, None, NVIDIA_SMI, False,
                       "%s is not on PATH; no other graphics-memory reader is wired here" % NVIDIA_SMI)
    try:
        p = run([exe, "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"],
                capture_output=True, text=True, encoding="utf-8", errors="replace")
    except OSError as e:
        return Reading(GRAPHICS, None, None, NVIDIA_SMI, False, "%s failed to start: %s" % (NVIDIA_SMI, e))
    text = (getattr(p, "stdout", "") or "").strip()
    if getattr(p, "returncode", 1) != 0 or not text:
        return Reading(GRAPHICS, None, None, NVIDIA_SMI, False,
                       "%s exited %s and said %r" % (NVIDIA_SMI, getattr(p, "returncode", None), text[:80]))
    parts = [x.strip() for x in text.splitlines()[0].split(",")]
    try:
        used, total = float(parts[0]), float(parts[1])
    except (ValueError, IndexError):
        return Reading(GRAPHICS, None, None, NVIDIA_SMI, False,
                       "%s answered %r, which is not used,total in MiB" % (NVIDIA_SMI, text[:80]))
    return Reading(GRAPHICS, max(0.0, total - used), total, NVIDIA_SMI, True)


class _MemStatus(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]


def read_system_free(stat=None) -> Reading:
    """Free physical memory, from GlobalMemoryStatusEx. `stat` is injectable for the same reason."""
    if stat is not None:
        free, total = stat()
        return Reading(SYSTEM, free, total, "supplied", True)
    try:
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        m = _MemStatus()
        m.dwLength = ctypes.sizeof(_MemStatus)
        if not k32.GlobalMemoryStatusEx(ctypes.byref(m)):
            return Reading(SYSTEM, None, None, "GlobalMemoryStatusEx", False,
                           "GlobalMemoryStatusEx failed (error %d)" % ctypes.get_last_error())
        mib = 1024.0 * 1024.0
        return Reading(SYSTEM, m.ullAvailPhys / mib, m.ullTotalPhys / mib,
                       "GlobalMemoryStatusEx", True)
    except (OSError, AttributeError) as e:            # not Windows, or no such export
        return Reading(SYSTEM, None, None, "GlobalMemoryStatusEx", False,
                       "system memory could not be read here: %s" % e)


# ------------------------------------------------------------------------ the rule --

def _fits(reading: Reading, per_process: float, reserve: float) -> int | None:
    if not reading.ok or reading.free_mib is None:
        return None
    if per_process <= 0:
        return None
    return int(max(0.0, reading.free_mib - reserve) // per_process)


def plan_limit(asked: int, need: Need | None, graphics: Reading, system: Reading,
               reserve_mib: float = RESERVE_MIB) -> Plan:
    """How many processes may start, and the sentence that says why.

    `asked` is a ceiling, never a floor. Both resources bound the answer; whichever binds is named
    in `basis`, so a caller can tell "the GPU is busy" from "the machine is out of RAM".
    """
    detail = {"asked": asked, "reserveMiB": reserve_mib,
              "graphics": graphics.describe(), "system": system.describe(),
              "needSource": need.source if need else None,
              "needGraphicsMiB": need.graphics_mib if need else None,
              "needSystemMiB": need.system_mib if need else None}
    if need is None:
        return Plan(0, True, UNREADABLE, detail=detail,
                    reason=("no per-process figure: it is measured from a solo run of the same job "
                            "or supplied by the caller, and this module keeps no constant for it"))

    fits = {GRAPHICS: _fits(graphics, need.graphics_mib, reserve_mib),
            SYSTEM: _fits(system, need.system_mib, reserve_mib)}
    detail["fits"] = dict(fits)
    unread = [r for r in (graphics, system) if not r.ok]
    if unread:
        return Plan(1, False, UNREADABLE, detail=detail,
                    reason=("; ".join(r.reason for r in unread)
                            + " — so this runs ONE process rather than the %d asked for, because a "
                              "limit that cannot be computed must not become the request "
                              "(reserve %.0f MiB)" % (asked, reserve_mib)))

    bound = min((v for v in fits.values() if v is not None), default=None)
    if bound is None:                                  # both readings ok but neither usable
        return Plan(1, False, UNREADABLE, detail=detail,
                    reason="neither memory reading yielded a usable number; running one process")
    if bound < 1:
        short = [k for k, v in fits.items() if v is not None and v < 1]
        return Plan(0, True, short[0], detail=detail,
                    reason=("one process of this job needs %.0f MiB of %s memory and only %.0f MiB "
                            "is free with %.0f MiB held back: not even one fits"
                            % (need.graphics_mib if short[0] == GRAPHICS else need.system_mib,
                               short[0],
                               (graphics if short[0] == GRAPHICS else system).free_mib or 0.0,
                               reserve_mib)))

    limit = min(asked, bound)
    basis = ASKED if limit == asked and bound >= asked else \
        (GRAPHICS if fits[GRAPHICS] == bound else SYSTEM)
    reason = ("%.0f MiB graphics and %.0f MiB system memory free, %.0f MiB held back, one process "
              "needs %.0f and %.0f (%s)"
              % (graphics.free_mib or 0.0, system.free_mib or 0.0, reserve_mib,
                 need.graphics_mib, need.system_mib, need.source))
    if basis != ASKED:
        reason += " — so %d, not the %d asked for" % (limit, asked)
    return Plan(limit, False, basis, reason, detail)


# ------------------------------------------------------------------- measuring need --

def measure_need(is_running, graphics_reader=read_graphics_free, system_reader=read_system_free,
                 interval=1.0, sleep=time.sleep) -> tuple[Need | None, dict]:
    """What one process of this job needed, sampled while it ran alone.

    `is_running()` says whether the solo job is still going. The figure returned is the PEAK DROP
    in free memory over the baseline taken before it started - a machine-wide delta attributed to
    this render, which is what it is: Windows does not report per-process graphics memory, so
    anything else running inflates it. The label travels with the number, and a caller-supplied
    figure always wins over this one.
    """
    g0, s0 = graphics_reader(), system_reader()
    samples = {"graphics": [], "system": [], "baselineGraphicsFreeMiB": g0.free_mib,
               "baselineSystemFreeMiB": s0.free_mib,
               "method": "peak drop in FREE memory over the pre-run baseline; machine-wide, "
                         "attributed to the solo render"}
    if not (g0.ok and s0.ok):
        samples["refused"] = "; ".join(r.reason for r in (g0, s0) if not r.ok)
        return None, samples
    while is_running():
        sleep(interval)
        g, s = graphics_reader(), system_reader()
        if g.ok and g.free_mib is not None:
            samples["graphics"].append(max(0.0, (g0.free_mib or 0.0) - g.free_mib))
        if s.ok and s.free_mib is not None:
            samples["system"].append(max(0.0, (s0.free_mib or 0.0) - s.free_mib))
    if not samples["graphics"] or not samples["system"]:
        samples["refused"] = "the job ended before a sample was taken"
        return None, samples
    need = Need(max(samples["graphics"]), max(samples["system"]), "measured")
    samples["peakGraphicsMiB"], samples["peakSystemMiB"] = need.graphics_mib, need.system_mib
    return need, samples
