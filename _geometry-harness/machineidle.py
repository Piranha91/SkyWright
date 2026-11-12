"""machineidle — is this machine idle enough to launch the game or time a heavy job?

    python machineidle.py [--min-free-gb 16] [--refuse-if LOCK ...] [--json OUT]

Exit codes: 0 idle; 3 not idle (the reasons are printed and written); 2 usage or the snapshot could not be read
(a reading missing or unreadable is named and, with --json, written as {"idle": false, "unavailable": ...}).

Why it exists. A launch or a timing on a busy machine confounds what it measures, and an idle check that
matches process NAMES misses the commonest heavy job here: a probe run as `dotnet <probe>.dll`, whose
process name is `dotnet`. So a probe is found by its process name, or by a dotnet host's PROGRAM argument
being its dll; a shell or script interpreter whose command line names a probe also blocks, as a possible batch
driver between two probe children (a caller an idle_lock hold admitted, `admitting_hold()`, may pass
count_shells=False: that hold's own check has run, and such a shell is then noted, not counted). Any other process whose command line merely names a probe (an editor, a
dotnet host of another program) does not. The game, Mod Organizer, the recorder and
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

#: Probe names: a process named one, a dotnet host whose program is `<name>.dll`, or (for shells) a whole word in the
#: command line. Any other `<word>probe` counts too (PROBE_FILE_RE for shells), so a new probe is not silently missed.
PROBES = ("contactprobe", "overlapprobe", "proofprobe", "floorgapprobe", "motionprobe", "viewprobe",
          "stateprobe", "crossprobe", "conflictdiff", "switchprobe", "scopeprobe", "navprobe", "seamprobe",
          "waterprobe", "geoprobe", "placeprobe", "floorprobe", "corridorprobe", "driftprobe")
PROBE_WORD_RE = re.compile(r"(?<![\w-])(" + "|".join(PROBES) + r")(?![\w-])", re.I)
PROBE_FILE_RE = re.compile(r"(?<![\w-])(\w+probe)\.(exe|dll)\b", re.I)
#: Shells and script interpreters: one whose command line names a probe may be a batch driver between two probe
#: children, so it still blocks, under its own wording. Every other process must BE a probe (name or dotnet program).
SHELLS = {"bash", "sh", "cmd", "powershell", "pwsh", "python", "pythonw", "py", "node"}
#: dotnet host options that take a value, skipped to find the program argument (`dotnet [exec] [opts] app.dll`).
DOTNET_VALUE_OPTS = {"--runtimeconfig", "--depsfile", "--additionalprobingpath", "--additional-deps",
                     "--fx-version", "--roll-forward"}


def is_probe_name(stem: str) -> bool:
    """A listed probe or any `<word>probe` (case-insensitive)."""
    s = stem.lower()
    return s in PROBES or re.fullmatch(r"\w+probe", s) is not None


def dotnet_program(cmd: str) -> str:
    """The basename of a dotnet host's program argument, lower-cased ('' if none): after `exec` and value options."""
    toks = [a or b for a, b in _TOKEN_RE.findall(cmd or "")][1:]
    i = 0
    if i < len(toks) and toks[i].lower() == "exec":
        i += 1
    while i < len(toks) and toks[i].startswith("-"):
        i += 2 if toks[i].lower() in DOTNET_VALUE_OPTS else 1
    return re.split(r"[\\/]", toks[i])[-1].lower() if i < len(toks) else ""

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

#: Stop on the first error: a CIM failure is otherwise non-terminating, and the unread values arrive as 0 and [].
PS_SNAPSHOT = (
    "$ErrorActionPreference='Stop'; "
    "$o=Get-CimInstance Win32_OperatingSystem; "
    "$p=Get-CimInstance Win32_Process | Select-Object Name,ProcessId,ParentProcessId,CommandLine,WorkingSetSize; "
    "[pscustomobject]@{freeKB=[int64]$o.FreePhysicalMemory; processes=@($p)} | ConvertTo-Json -Depth 3 -Compress")


class SnapshotUnavailable(RuntimeError):
    """A reading the decision needs could not be taken. Missing data is not zero load."""


def snapshot() -> dict:
    r = subprocess.run(["powershell", "-NoProfile", "-Command", PS_SNAPSHOT], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=120)
    err = (r.stderr or "").strip().splitlines()
    why = f" ({err[0].strip()[:200]})" if err else ""
    if r.returncode != 0 or not r.stdout.strip():
        raise SnapshotUnavailable(f"process snapshot failed (exit {r.returncode}){why}")
    try:
        doc = json.loads(r.stdout)
    except ValueError as ex:
        raise SnapshotUnavailable(f"process snapshot unreadable ({ex}){why}") from None
    free_kb = doc.get("freeKB") if isinstance(doc, dict) else None
    if isinstance(free_kb, bool) or not isinstance(free_kb, int) or free_kb <= 0:
        raise SnapshotUnavailable(f"free physical memory reading unavailable (freeKB {free_kb!r}){why}")
    if not isinstance(doc.get("processes"), list) or not doc["processes"]:
        raise SnapshotUnavailable(f"process list reading unavailable (no processes returned){why}")
    procs = [{"name": p.get("Name") or "", "pid": p.get("ProcessId"), "ppid": p.get("ParentProcessId"),
              "cmd": p.get("CommandLine") or "", "wsMB": round((p.get("WorkingSetSize") or 0) / 2**20)}
             for p in doc.get("processes") or []]
    return {"freeMB": round(free_kb / 1024), "processes": procs, "self": os.getpid()}


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


#: The environment an idle_lock hold gives its job: the slot's token and the slot file that names it.
SLOT_ENV, SLOT_FILE_ENV = "IDLE_LOCK_SLOT", "IDLE_LOCK_SLOT_FILE"


def _pid_alive(pid) -> bool:
    if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 0:
        return False
    if os.name == "nt":
        import ctypes
        k = ctypes.windll.kernel32
        h = k.OpenProcess(0x1000, False, pid)
        if not h:
            return False
        code = ctypes.c_ulong()
        ok = k.GetExitCodeProcess(h, ctypes.byref(code))
        k.CloseHandle(h)
        return bool(ok) and code.value == 259
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def admitting_hold(env=None, alive=_pid_alive) -> str | None:
    """One line naming the idle_lock hold that admitted this process, or None. Admitted means the hold's job
    environment names a slot file whose token is the environment's and whose holder pid is running: that hold has
    already passed its own idle check, which notes a probe-naming shell rather than counting it."""
    env = os.environ if env is None else env
    tok, path = env.get(SLOT_ENV), env.get(SLOT_FILE_ENV)
    if not tok or not path:
        return None
    try:
        c = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    if not isinstance(c, dict) or c.get("token") != tok or not alive(c.get("pid")):
        return None
    return f"admitted by idle_lock hold pid {c.get('pid')} ({c.get('purpose')}; slot {path})"


def decide(snap: dict, *, min_free_gb: float = DEFAULT_MIN_FREE_GB, refuse_if=(), exists=os.path.exists,
           count_shells: bool = True) -> dict:
    """The verdict for one snapshot: {"idle": bool, "reasons": [...], "freeMB": n, "blocking": [...], "noted": [...]}.
    With count_shells False (a caller an idle_lock hold admitted) a shell naming a probe is noted, not counted."""
    reasons, blocking, noted = [], [], []
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
        prog = dotnet_program(cmd) if base == "dotnet" else ""
        if base in BLOCKING_NAMES:
            why = f"{BLOCKING_NAMES[base]} running ({name}, by process name)"
        elif is_probe_name(base):
            why = f"a probe running ({base}, by process name)"
        elif prog.endswith(".dll") and is_probe_name(prog[:-4]):
            why = f"a probe running ({prog[:-4]}, by command line, process {name})"
        elif base in SHELLS and (PROBE_WORD_RE.search(cmd) or PROBE_FILE_RE.search(cmd)):
            m = PROBE_WORD_RE.search(cmd) or PROBE_FILE_RE.search(cmd)
            why = f"a shell or script naming a probe (may be a batch driver) ({m.group(1)}, process {name})"
            if not count_shells:
                noted.append(f"pid {p.get('pid')}: {why}; not counted: an idle_lock hold admitted this caller")
                why = None
        elif base == "dotnet" and dotnet_first_arg(cmd) in DOTNET_BUILD_ARGS:
            why = f"a build running (dotnet build host, by command line)"
        if why:
            blocking.append({"pid": p.get("pid"), "name": name, "wsMB": p.get("wsMB"), "why": why,
                             "cmd": cmd[:300]})
            reasons.append(f"pid {p.get('pid')}: {why}")
    for lock in refuse_if or ():
        if exists(lock):
            reasons.append(f"lock present: {lock}")
    return {"idle": not reasons, "reasons": reasons, "freeMB": free, "blocking": blocking, "noted": noted,
            "minFreeGB": min_free_gb, "locksChecked": list(refuse_if or ())}


def check(*, min_free_gb: float = DEFAULT_MIN_FREE_GB, refuse_if=(), count_shells: bool = True) -> dict:
    """Snapshot now and decide. The snapshot's process count is kept with the verdict."""
    snap = snapshot()
    verdict = decide(snap, min_free_gb=min_free_gb, refuse_if=refuse_if, count_shells=count_shells)
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
            reason = f"snapshot unavailable: {ex}"
            print(f"REFUSED: {reason}")
            if a.json:
                Path(a.json).write_text(json.dumps({"idle": False, "unavailable": reason, "reasons": [reason],
                                                    "freeMB": None, "processCount": None, "blocking": [],
                                                    "minFreeGB": a.min_free_gb, "locksChecked": a.refuse_if},
                                                   indent=1), encoding="utf-8")
                run.artifact(a.json)
            run.refuse(reason, 2)
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
