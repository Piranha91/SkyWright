"""evalrecords_workers - how many of a capture's arms read at once, and running them side by side.

evalrecords' capture calls this when asked for more than one worker. Each arm reads in its own process with its own
reader, under the machine slot its caller holds. How many run at once comes from each arm's estimated need against
memory: the estimates are summed as if every peak coincides, and a watcher stops the newest arm if memory runs low.
With nothing running the next arm always starts, so there is always one worker and never a failure for lack of memory.

An arm stopped for memory, or refused or crashed while others ran, reads again alone once, and that attempt's answer
stands. A deadline is not retried. Every attempt is recorded in capture-workers.json.

Not part of the checkpoint key: nothing here touches an answer, so a change here rereads nothing.
"""
from __future__ import annotations

import ctypes
import csv
import json
import os
import subprocess
import sys
import threading
import time
import uuid
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
from runlog import Runner, store_dir  # noqa: E402

# what one arm's reader needs, in MB, from its evaluated plugins' bytes; fitted on serial captures, margins above the
# worst under-estimate with one subject left out
WS_BASE, WS_PER_MB, WS_MARGIN = 1195, 177, 1000
COMMIT_BASE, COMMIT_PER_MB, COMMIT_MARGIN = 1336, 375, 1500
PYTHON_MB = 700                 # an arm's Python, with transcripts written as they arrive
RESERVE_MIN_MB = 6000           # memory left to everything else, at least 10% of the machine
OS_ALLOWANCE_MB = 4000          # what a machine's physical memory can't give any capture, even with every program closed
LOW_AVAILABLE_MB = 3000         # the watcher stops the newest arm below either of these
LOW_COMMIT_MB = 2000
STOP_GRACE_S = 5                # a stopped arm's memory takes a moment to come back
MOST_BY_DEFAULT = 5             # the most readers measured; a number on the command line may ask for more
MEMORY_FILE_ENV = "EVALRECORDS_MEMORY_FILE"     # tests name a JSON file of readings here
NOT_CLOSABLE = {"memory compression", "system", "registry", "secure system", "system idle process", "smss.exe", "csrss.exe", "wininit.exe",
                "winlogon.exe", "services.exe", "lsass.exe", "dwm.exe", "svchost.exe", "vmmem", "vmmemwsl"}


# ---------------------------------------------------------------- memory

@dataclass(frozen=True)
class Memory:
    avail: int
    commit: int
    total: int


class _MS(ctypes.Structure):
    _fields_ = [("l", ctypes.c_ulong), ("load", ctypes.c_ulong)] + [(n, ctypes.c_ulonglong) for n in ("tp", "ap", "tpf", "apf", "tv", "av", "aev")]


def read_memory() -> Memory | None:
    """What the machine has now in MB, or None when it can't be read."""
    named = os.environ.get(MEMORY_FILE_ENV)
    if named:
        d = json.loads(Path(named).read_text(encoding="utf-8"))
        return Memory(int(d["availMB"]), int(d["commitMB"]), int(d["totalMB"]))
    if os.name != "nt":
        try:
            rows = dict(ln.split(":", 1) for ln in Path("/proc/meminfo").read_text().splitlines() if ":" in ln)
            kb = lambda k: int(rows[k].split()[0]) // 1024  # noqa: E731
            return Memory(kb("MemAvailable"), kb("MemAvailable") + kb("SwapFree"), kb("MemTotal"))
        except (OSError, KeyError, ValueError):
            return None
    m = _MS()
    m.l = ctypes.sizeof(_MS)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m)):
        return None
    return Memory(m.ap >> 20, m.apf >> 20, m.tp >> 20)


def programs_in(tasklist_csv: str, n=3) -> list:
    """The largest programs in `tasklist /fo csv /nh` text by working set, [(name, MB)], without the system's own, which nobody can close."""
    by = {}
    for row in csv.reader(tasklist_csv.splitlines()):
        if len(row) >= 5 and row[0].lower() not in NOT_CLOSABLE:
            digits = "".join(c for c in row[4] if c.isdigit())
            if digits:
                by[row[0]] = by.get(row[0], 0) + int(digits) // 1024
    return sorted(by.items(), key=lambda kv: -kv[1])[:n]


def largest_programs(n=3) -> list:
    """The largest running programs a user could close, for the warning; empty when the list can't be read."""
    if os.name != "nt":
        return []
    try:
        return programs_in(subprocess.run(["tasklist", "/fo", "csv", "/nh"], capture_output=True, text=True, errors="replace", timeout=30).stdout, n)
    except (OSError, subprocess.SubprocessError):
        return []


def process_counters(proc=None) -> dict:
    """Peak memory, read volume and cycles of a finished or running process (the current one without `proc`); {} off Windows or on failure."""
    if os.name != "nt":
        return {}
    from ctypes import wintypes

    class PMC(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD), ("peakWs", ctypes.c_size_t), ("ws", ctypes.c_size_t), ("a", ctypes.c_size_t), ("b", ctypes.c_size_t),
                    ("c", ctypes.c_size_t), ("d", ctypes.c_size_t), ("commit", ctypes.c_size_t), ("peakCommit", ctypes.c_size_t)]

    class IO(ctypes.Structure):
        _fields_ = [(n, ctypes.c_ulonglong) for n in ("readOps", "writeOps", "otherOps", "readBytes", "writeBytes", "otherBytes")]

    try:
        k = ctypes.WinDLL("kernel32", use_last_error=True)
        k.GetCurrentProcess.restype = ctypes.c_void_p
        h = ctypes.c_void_p(int(proc._handle)) if proc is not None else ctypes.c_void_p(k.GetCurrentProcess())
        m = PMC()
        m.cb = ctypes.sizeof(PMC)
        if not ctypes.WinDLL("psapi").GetProcessMemoryInfo(h, ctypes.byref(m), m.cb):
            return {}
        doc = {"peakCommitMB": m.peakCommit >> 20, "peakWsMB": m.peakWs >> 20}
        io = IO()
        if k.GetProcessIoCounters(h, ctypes.byref(io)):
            doc.update(readBytes=io.readBytes, readOps=io.readOps)
        cycles = ctypes.c_ulonglong()
        if k.QueryProcessCycleTime(h, ctypes.byref(cycles)):
            doc["cycles"] = cycles.value
        return doc
    except (OSError, AttributeError, ValueError):
        return {}


# ---------------------------------------------------------------- the choice

def parse_workers(value, name="--workers"):
    """`auto` or a whole number from 1 up; anything else is one plain sentence (ValueError), before any arm reads."""
    text = str(value).strip()
    if text == "auto":
        return "auto"
    if text.isdigit() and int(text) >= 1:
        return int(text)
    raise ValueError(f"{name} is {value!r}; it takes auto or a whole number from 1 up, and 1 reads one arm at a time.")


def arm_need(arm: dict, baseline) -> dict:
    """One arm's estimated peak in MB, before it runs, from its evaluated plugins' bytes."""
    base = set(baseline)
    paths = {p.get("name"): p.get("path") for p in (arm.get("requested") or {}).get("plugins") or []}
    n = 0
    for p in arm.get("plugins") or []:
        if p not in base and paths.get(p):
            try:
                n += os.path.getsize(paths[p])
            except OSError:
                pass
    mb = n / 1e6
    return {"evaluatedMB": round(mb, 1), "ws": round(WS_BASE + WS_PER_MB * mb + PYTHON_MB + WS_MARGIN),
            "commit": round(COMMIT_BASE + COMMIT_PER_MB * mb + PYTHON_MB + COMMIT_MARGIN)}


def reserve(total: int) -> int:
    return max(RESERVE_MIN_MB, total // 10)


def how_many_fit(needs: list, ws_budget: float, commit_budget: float, most: int) -> int:
    """Largest first, all at once, as if their peaks coincide; at least one."""
    n = w = c = 0
    for x in sorted(needs, key=lambda x: -x["ws"]):
        if n >= most or w + x["ws"] > ws_budget or c + x["commit"] > commit_budget:
            break
        w, c, n = w + x["ws"], c + x["commit"], n + 1
    return max(n, 1)


def choose(requested, needs: list, mem: Memory | None, cpus: int | None = None) -> dict:
    """How many arms read at once, which of Piranha's three cases this is, and the warning if other programs hold memory."""
    cpus = cpus or os.cpu_count() or 1
    most = min(max(cpus // 3, 1), MOST_BY_DEFAULT) if requested == "auto" else int(requested)
    want = min(most, len(needs))
    if mem is None:
        return {"requested": requested, "workers": 1, "case": "memory-unread", "memory": None, "warning":
                "This machine's memory could not be read, so the capture reads one arm at a time."}
    keep = reserve(mem.total)
    by_avail = how_many_fit(needs, mem.avail - keep, mem.commit - keep, want)
    by_total = how_many_fit(needs, mem.total - keep - OS_ALLOWANCE_MB, float("inf"), want)
    doc = {"requested": requested, "workers": min(by_avail, by_total), "memory": {"availMB": mem.avail, "commitMB": mem.commit, "totalMB": mem.total},
           "reserveMB": keep, "allowedByPhysical": by_total, "allowedByAvailable": by_avail, "warning": None}
    if want <= 1:
        doc["case"] = "one-asked-or-needed"
    elif by_total <= 1:
        doc["case"] = "absent"
    elif by_avail < by_total:
        doc["case"] = "held-by-others"
        by_physical = how_many_fit(needs, mem.avail - keep, float("inf"), want)
        by_commit = how_many_fit(needs, float("inf"), mem.commit - keep, want)
        physical = f"{mem.avail} MB of physical memory is free"
        commit = f"{mem.commit} MB of commit (physical memory plus page file) is free"
        doc["limit"] = "both" if by_physical == by_commit else "physical" if by_physical < by_commit else "commit"
        limit = {"both": f"{physical} and {commit}", "physical": physical, "commit": commit}[doc["limit"]]
        names = ", ".join(f"{n} ({mb} MB)" for n, mb in largest_programs()) or "other programs"
        doc["warning"] = (f"Other programs hold memory, so the capture reads {by_avail} arm(s) at a time rather than {by_total}: only {limit}. "
                          f"Largest now: {names}. Close some to read faster next time, or set recordWorkers to 1 to read one at a time.")
    else:
        doc["case"] = "free"
    return doc


# ---------------------------------------------------------------- running them

class Attempt:
    launched = 0

    def __init__(self, arm_id: str, n: int, company: bool):
        self.arm, self.n, self.company = arm_id, n, company
        Attempt.launched += 1
        self.seq = Attempt.launched
        self.stop = threading.Event()
        self.stopped_for_memory = False
        self.started = time.time()
        self.ended = None
        self.result = None
        self.thread = None
        self.doc_path = None
        self.log_path = None


def _attempt_record(a: Attempt, outcome: str, doc: dict | None, reason: str | None) -> dict:
    rec = {"attempt": a.n, "launched": a.seq, "startedEpoch": round(a.started, 1), "endedEpoch": round(a.ended or time.time(), 1), "besideOthers": a.company, "outcome": outcome,
           "childPid": (a.result or {}).get("ownedPid"), "exit": (a.result or {}).get("exitCode"), "log": str(a.log_path) if a.log_path else None}
    if reason:
        rec["reason"] = reason
    if doc:
        rec["reader"] = doc.get("reader")
        rec["pythonPeak"] = doc.get("python")
    return rec


def read_doc(path: Path) -> dict | None:
    """An attempt's document, or None when it is absent or unreadable: either way that attempt left no result."""
    try:
        doc = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return doc if isinstance(doc, dict) else None


def judge(arm_id: str, doc: dict | None, exit_code, kept) -> tuple:
    """What one attempt came to: ("finished", value), ("refused", value) or ("none", None). Finished means all of it: exit 0, a document
    naming its imports, every import on disk, and `kept(arm_id, value)`, its checkpoint saved. Nothing less reports an arm as read."""
    v = ((doc or {}).get("summary") or {}).get(arm_id)
    if not isinstance(v, dict):
        return "none", None
    if "refused" in v:
        return "refused", v
    imports = v.get("imports")
    if exit_code == 0 and isinstance(imports, list) and all(Path(p).is_file() for p in imports) and kept(arm_id, v):
        return "finished", v
    return "none", None


def run_side_by_side(arms: list, needs: dict, choice: dict, build_argv, out: Path, on_final, kept, *, poll_s=1.0, memory=read_memory) -> dict:
    """Read `arms` (largest need first) with up to choice["workers"] at once. `build_argv(arm, doc_path)` gives one arm's capture command, writing doc_path;
    `kept(arm_id, value)` says whether a finished arm's checkpoint is saved.
    Returns {armId: {"final": summary value, "attempts": [records], "curedAlone": reason or None}}."""
    work = out / "workers"
    work.mkdir(parents=True, exist_ok=True)
    env = {"HOUSECARL_RUNLOG_DIR": str(store_dir())}
    queue = sorted(arms, key=lambda a: -needs[a["armId"]]["ws"])    # sorted() is stable: plan order breaks a tie
    by_id = {a["armId"]: a for a in arms}
    alone, active, state = [], [], {a["armId"]: {"final": None, "attempts": [], "curedAlone": None} for a in arms}
    cap = choice["workers"]
    last_stop = 0.0
    keep = choice.get("reserveMB") or RESERVE_MIN_MB
    budget = None
    # this capture's own name on every attempt's files, so an earlier capture's document in the same folder is never read
    fresh = uuid.uuid4().hex[:8]

    def launch(arm_id, company):
        n = len(state[arm_id]["attempts"]) + 1
        a = Attempt(arm_id, n, company)
        a.doc_path = work / f"{arm_id}-{fresh}-attempt-{n}.json"
        job_id = f"arm-{arm_id}-{fresh}-{n}"
        a.log_path = work / f"{job_id}.txt"
        job = {"id": job_id, "argv": [str(x) for x in build_argv(by_id[arm_id], a.doc_path)], "cwd": os.getcwd(), "env": env}

        def watch(ev):
            if a.stop.is_set() and ev.get("kind") in ("sample", "output"):
                a.stopped_for_memory = True
                return {"stop": "memory", "message": "memory ran low with other arms reading", "metrics": {}}
            return None

        def body():
            try:
                a.result = Runner("evalrecords-arm", out_dir=work).run([job], echo=False, observer=watch)[0]
            except Exception as ex:  # recorded as the attempt's failure, read by the scheduler below
                a.result = {"exitCode": None, "error": f"{type(ex).__name__}: {ex}"}
            a.ended = time.time()
        a.thread = threading.Thread(target=body, daemon=True)
        a.thread.start()
        active.append(a)
        print(f"{arm_id}: attempt {n} started, {len(active)} reading", flush=True)

    def settle(a: Attempt):
        s = state[a.arm]
        doc = read_doc(a.doc_path)
        verdict, v = judge(a.arm, doc, (a.result or {}).get("exitCode"), kept)
        if a.stopped_for_memory and verdict == "none":
            s["attempts"].append(_attempt_record(a, "stopped-for-memory", doc, "memory ran low with other arms reading"))
            alone.append(a.arm)
            print(f"{a.arm}: stopped for memory; it reads again alone", flush=True)
            return
        if verdict == "none":
            why = f"the arm's capture process ended with exit {(a.result or {}).get('exitCode')} and no result; read {a.log_path}."
            v = {"refused": why}
        refused = v.get("refused")
        if refused and "deadline" not in str(refused) and a.company:
            s["attempts"].append(_attempt_record(a, "refused-beside-others", doc, str(refused)))
            alone.append(a.arm)
            print(f"{a.arm}: refused beside other arms; it reads again alone: {refused}", flush=True)
            return
        s["attempts"].append(_attempt_record(a, "refused" if refused else "finished", doc, str(refused) if refused else None))
        earlier = [r for r in s["attempts"] if r["outcome"] == "refused-beside-others"]
        if not refused and earlier:
            s["curedAlone"] = earlier[0]["reason"]
        s["final"] = v
        on_final(a.arm)

    while queue or alone or active:
        for a in [x for x in active if not x.thread.is_alive()]:
            active.remove(a)
            settle(a)
        now = memory()
        running = [x for x in active if not x.stop.is_set()]
        if now is not None and len(running) > 1 and time.monotonic() - last_stop >= STOP_GRACE_S and (now.avail < LOW_AVAILABLE_MB or now.commit < LOW_COMMIT_MB):
            victim = max(running, key=lambda x: x.seq)
            victim.stop.set()
            last_stop = time.monotonic()
            cap = len(running) - 1
            print(f"{victim.arm}: memory low ({now.avail} MB free, {now.commit} MB of commit); stopping the newest arm", flush=True)
        if not active:
            budget = (now.avail - keep, now.commit - keep) if now is not None else (0, 0)
            if queue:
                launch(queue.pop(0)["armId"], False)
            elif alone:
                launch(alone.pop(0), False)
                time.sleep(poll_s)
                continue
        # a smaller arm may start beside the running ones when the next largest doesn't fit
        while queue and len(active) < cap:
            used_w = sum(needs[x.arm]["ws"] for x in active)
            used_c = sum(needs[x.arm]["commit"] for x in active)
            fit = next((q for q in queue if used_w + needs[q["armId"]]["ws"] <= budget[0] and used_c + needs[q["armId"]]["commit"] <= budget[1]), None)
            if fit is None:
                break
            queue.remove(fit)
            for x in active:
                x.company = True
            launch(fit["armId"], True)
        time.sleep(poll_s)
    return state
