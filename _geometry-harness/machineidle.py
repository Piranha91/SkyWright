"""machineidle — is this machine idle enough to launch the game or time a heavy job?

    python machineidle.py [--min-free-gb 16] [--refuse-if LOCK ...] [--json OUT]

Exit codes: 0 idle; 3 not idle (the reasons are printed and written); 2 usage or the snapshot could not be read.

Why it exists. A launch or a timing on a busy machine confounds what it measures, and an idle check that
matches process NAMES misses the commonest heavy job here: a probe run as `dotnet <probe>.dll`, whose
process name is `dotnet`. So probes are found by COMMAND LINE. The game, Mod Organizer, the recorder and
the build servers are found by process NAME only: an idle IDE language server carries "msbuild" in its
command line, and matching that would refuse every launch on a machine with the editor open.

The snapshot (process names, ids, parents, command lines, free physical memory) is read once through
CIM; `decide()` is a pure function of it, so every rule is a fixture test. The calling process and its
ancestors are never counted: a driver's own command line may name what it is about to run.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))

#: Probe executables, matched as whole words anywhere in a command line (case-insensitive). Any other
#: `<name>probe.exe|.dll` is matched by PROBE_FILE_RE as well, so a new probe is not silently missed.
PROBES = ("contactprobe", "overlapprobe", "proofprobe", "floorgapprobe", "motionprobe", "viewprobe",
          "stateprobe", "crossprobe", "conflictdiff", "switchprobe", "scopeprobe", "navprobe", "seamprobe",
          "waterprobe", "geoprobe", "placeprobe", "floorprobe", "corridorprobe", "driftprobe")
PROBE_WORD_RE = re.compile(r"(?<![\w-])(" + "|".join(PROBES) + r")(?![\w-])", re.I)
PROBE_FILE_RE = re.compile(r"(?<![\w-])(\w+probe)\.(exe|dll)\b", re.I)

#: Matched on the process NAME only (case-insensitive, with or without `.exe`).
BLOCKING_NAMES = {
    "skyrimse": "the game", "skyrimvr": "the game", "skse64_loader": "the game loader",
    "sksevr_loader": "the game loader", "modorganizer": "Mod Organizer", "obs64": "the recorder",
    "msbuild": "a build server", "vbcscompiler": "a build server",
}
#: A `dotnet` host whose first argument is a build verb or MSBuild itself (worker nodes run `dotnet MSBuild.dll`).
DOTNET_BUILD_ARGS = {"msbuild.dll", "build", "msbuild", "test", "publish", "run"}
_TOKEN_RE = re.compile(r'"([^"]*)"|(\S+)')


def dotnet_first_arg(cmd: str) -> str:
    """The basename of the first argument after the program, quoted or not, lower-cased ('' if none)."""
    toks = [a or b for a, b in _TOKEN_RE.findall(cmd or "")]
    if len(toks) < 2:
        return ""
    return re.split(r"[\\/]", toks[1])[-1].lower()


DEFAULT_MIN_FREE_GB = 16.0

PS_SNAPSHOT = (
    "$o=Get-CimInstance Win32_OperatingSystem; "
    "$p=Get-CimInstance Win32_Process | Select-Object Name,ProcessId,ParentProcessId,CommandLine,WorkingSetSize; "
    "[pscustomobject]@{freeKB=[int64]$o.FreePhysicalMemory; processes=@($p)} | ConvertTo-Json -Depth 3 -Compress")


def snapshot() -> dict:
    r = subprocess.run(["powershell", "-NoProfile", "-Command", PS_SNAPSHOT], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=120)
    if r.returncode != 0 or not r.stdout.strip():
        raise RuntimeError(f"process snapshot failed (exit {r.returncode}): {r.stderr.strip()[:300]}")
    doc = json.loads(r.stdout)
    procs = [{"name": p.get("Name") or "", "pid": p.get("ProcessId"), "ppid": p.get("ParentProcessId"),
              "cmd": p.get("CommandLine") or "", "wsMB": round((p.get("WorkingSetSize") or 0) / 2**20)}
             for p in doc.get("processes") or []]
    return {"freeMB": round(int(doc.get("freeKB") or 0) / 1024), "processes": procs, "self": os.getpid()}


def _ancestors(procs: list[dict], pid) -> set:
    by_pid = {p["pid"]: p for p in procs}
    out, cur = set(), pid
    while cur in by_pid and cur not in out:
        out.add(cur)
        cur = by_pid[cur].get("ppid")
    return out


def _base(name: str) -> str:
    n = name.lower()
    return n[:-4] if n.endswith(".exe") else n


def decide(snap: dict, *, min_free_gb: float = DEFAULT_MIN_FREE_GB, refuse_if=(), exists=os.path.exists) -> dict:
    """The verdict for one snapshot: {"idle": bool, "reasons": [...], "freeMB": n, "blocking": [...]}."""
    reasons, blocking = [], []
    own = _ancestors(snap.get("processes") or [], snap.get("self"))
    free = snap.get("freeMB") or 0
    if free < min_free_gb * 1024:
        reasons.append(f"free physical memory {free} MB is below {min_free_gb:g} GB")
    for p in snap.get("processes") or []:
        if p.get("pid") in own:
            continue
        name, cmd = p.get("name") or "", p.get("cmd") or ""
        why = None
        base = _base(name)
        if base in BLOCKING_NAMES:
            why = f"{BLOCKING_NAMES[base]} running ({name}, by process name)"
        elif PROBE_WORD_RE.search(cmd) or PROBE_FILE_RE.search(cmd):
            m = PROBE_WORD_RE.search(cmd) or PROBE_FILE_RE.search(cmd)
            why = f"a probe running ({m.group(1)}, by command line, process {name})"
        elif base == "dotnet" and dotnet_first_arg(cmd) in DOTNET_BUILD_ARGS:
            why = f"a build running (dotnet build host, by command line)"
        if why:
            blocking.append({"pid": p.get("pid"), "name": name, "wsMB": p.get("wsMB"), "why": why,
                             "cmd": cmd[:300]})
            reasons.append(f"pid {p.get('pid')}: {why}")
    for lock in refuse_if or ():
        if exists(lock):
            reasons.append(f"lock present: {lock}")
    return {"idle": not reasons, "reasons": reasons, "freeMB": free, "blocking": blocking,
            "minFreeGB": min_free_gb, "locksChecked": list(refuse_if or ())}


def check(*, min_free_gb: float = DEFAULT_MIN_FREE_GB, refuse_if=()) -> dict:
    """Snapshot now and decide. The snapshot's process count is kept with the verdict."""
    snap = snapshot()
    verdict = decide(snap, min_free_gb=min_free_gb, refuse_if=refuse_if)
    verdict["processCount"] = len(snap["processes"])
    return verdict


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="refuse a launch or timing on a machine that is not idle")
    ap.add_argument("--min-free-gb", type=float, default=DEFAULT_MIN_FREE_GB,
                    help="the free physical memory floor in GB (default 16)")
    ap.add_argument("--refuse-if", action="append", default=[], metavar="LOCK",
                    help="a lock file whose presence refuses (repeatable)")
    ap.add_argument("--json", help="write the verdict here as JSON")
    a = ap.parse_args(argv)
    from runlog import Run
    with Run("machineidle", stage_source="counted") as run:
        run.stage("snapshot", total=1, unit="snapshots")
        try:
            v = check(min_free_gb=a.min_free_gb, refuse_if=a.refuse_if)
        except Exception as ex:
            print(f"REFUSED: {ex}")
            run.refuse(f"snapshot failed: {ex}", 2)
            return 2
        run.advance()
        if a.json:
            Path(a.json).write_text(json.dumps(v, indent=1), encoding="utf-8")
            run.artifact(a.json)
        print(("IDLE" if v["idle"] else "NOT IDLE") + f": free {v['freeMB']} MB")
        for r in v["reasons"]:
            print("  " + r)
        if not v["idle"]:
            run.refuse("machine not idle: " + "; ".join(v["reasons"])[:400], 3)
            return 3
        return 0


if __name__ == "__main__":
    sys.exit(main())
