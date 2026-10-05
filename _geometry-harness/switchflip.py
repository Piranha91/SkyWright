"""switchflip — the live driver of the runtime state-switch check (`runtime.state-switch-flip`).

    python switchflip.py validate --plan FLIPPLAN
    python switchflip.py batch    --plan FLIPPLAN --out DIR --instance MO2_DIR --shortcut LNK --lock GAME_LOCK
                                  [--refuse-if LOCK ...] [--evalrun-plan RUN/plan.json] [--min-free-gb 16]
                                  [--port 8920] [--crash-dir DIR] [--launch-minutes 25] [--dry]
    python switchflip.py export   --block DIR --arm ARM --out import.json

What it answers. Whether flipping the enable switches a mod's scripts flip, in the scripts' own order and with the
target state they set, crashes or stalls the game under an evaluated load order. A static state check renders the
geometry of a flipped marker; it cannot show what the engine does while it switches a set on. Two mods that each work
alone can, together, make that moment fail with no record that looks wrong.

The flip plan (schema 1; `validate_plan` says exactly what is refused) is written by the evaluator from the switch
census and the bound scripts' source:
  {schemaVersion: 1,
   binding: null | {planId, checkId},                       # an evalrun plan this block is launched for
   arms: [{id, profile, evalArmId?}],                       # evalArmId: the plan's arm, when bound
   conditions: [{id, arm, arrival: {cell}, settleSeconds, windowSeconds (>= 60), replicates (>= 1),
                 expect?: "fail"|"complete",                # a known-answer block: a contrary outcome stops it
                 canaryAfter?: bool,
                 calls: [{ref: "XXXXXX:Plugin.esp", target: "enabled"|"disabled", source: "<where the order is from>"}]}],
   canary?: {consoleCommand, label},                        # a known crash, sent after a launch's flip is classified
   schedule?: [[conditionId, replicate], ...]}              # default: replicates interleaved across conditions

One block holds the lock file for every launch in it (created only when absent, removed in `finally`). Before the lock
and before every launch the machine is measured idle (machineidle.py: probes by command line; the game, MO2, the
recorder and build servers by process name; free memory). Each launch is a `scripts/runlog.py` Runner child with a
deadline, and runs: profile snapshot, launch outside this shell's job object, readiness, strict game-capture
recording, one arrival (a new game and `coc`), the calls (send and return times, a readback before and after each
call), the observation window (heartbeat, process liveness), crash-log capture, teardown, MO2 close, profile
restore proof. The classification is switchflip_outcome.classify, written beside the events it was read from.

Exit codes: 0 block finished; 2 usage or a refused input; 3 refused by the idle gate or the lock; 4 a launch failed
without a receipt, or a known-answer block met a contrary outcome (the block stopped there).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import traceback
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
sys.path.insert(0, str(REPO / "scripts"))
sys.path.insert(0, str(HERE))
DBHARNESS_DIR = REPO / ".claude" / "skills" / "devbench-in-game-testing" / "scripts"

import switchflip_outcome as O  # noqa: E402
import machineidle  # noqa: E402

PLAN_SCHEMA = 1
RECEIPT_SCHEMA = 1
REF_RE = re.compile(r"^[0-9A-Fa-f]{6}:[^:\\/]+\.(esp|esm|esl)$")
TARGETS = ("enabled", "disabled")
CALL_SPACING_S = 0.3
EXIT_REFUSED_INPUT, EXIT_NOT_IDLE, EXIT_STOPPED = 2, 3, 4


def utcnow() -> str:
    return datetime.now(timezone.utc).isoformat()


def sha256_file(p) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ------------------------------------------------------------------------------------------ the flip plan --

def validate_plan(doc) -> list[str]:
    """Every problem with a flip plan, as sentences; [] means valid. Nothing launches on a plan with a problem."""
    p = []
    if not isinstance(doc, dict):
        return ["the flip plan is not a JSON object"]
    if doc.get("schemaVersion") != PLAN_SCHEMA:
        p.append(f"schemaVersion must be {PLAN_SCHEMA}")
    binding = doc.get("binding")
    if binding is not None and not (isinstance(binding, dict) and binding.get("planId") and binding.get("checkId")):
        p.append("binding must be null or {planId, checkId}")
    arms = {}
    for i, a in enumerate(doc.get("arms") or []):
        if not isinstance(a, dict) or not a.get("id") or not a.get("profile"):
            p.append(f"arms[{i}] needs id and profile")
            continue
        if a["id"] in arms:
            p.append(f"duplicate arm id {a['id']!r}")
        if binding and not a.get("evalArmId"):
            p.append(f"arm {a['id']}: a bound plan names each arm's evalArmId")
        arms[a["id"]] = a
    if not arms:
        p.append("no arms")
    conds = doc.get("conditions") or []
    if not conds:
        p.append("no conditions")
    ids = set()
    for i, c in enumerate(conds):
        if not isinstance(c, dict) or not c.get("id"):
            p.append(f"conditions[{i}] has no id")
            continue
        cid = c["id"]
        if cid in ids:
            p.append(f"duplicate condition id {cid!r}")
        ids.add(cid)
        if c.get("arm") not in arms:
            p.append(f"condition {cid}: unknown arm {c.get('arm')!r}")
        if not ((c.get("arrival") or {}).get("cell")):
            p.append(f"condition {cid}: arrival.cell is required")
        w = c.get("windowSeconds")
        if not isinstance(w, (int, float)) or isinstance(w, bool) or w < O.WINDOW_S:
            p.append(f"condition {cid}: windowSeconds must be at least {O.WINDOW_S:g}")
        s = c.get("settleSeconds", 0)
        if not isinstance(s, (int, float)) or isinstance(s, bool) or s < 0:
            p.append(f"condition {cid}: settleSeconds must be a non-negative number")
        r = c.get("replicates")
        if not isinstance(r, int) or isinstance(r, bool) or r < 1:
            p.append(f"condition {cid}: replicates must be a positive integer")
        if c.get("expect") not in (None, "fail", "complete"):
            p.append(f"condition {cid}: expect must be fail, complete or absent")
        if c.get("canaryAfter") and not ((doc.get("canary") or {}).get("consoleCommand")):
            p.append(f"condition {cid}: canaryAfter needs a top-level canary.consoleCommand")
        calls = c.get("calls") or []
        if not calls:
            p.append(f"condition {cid}: no calls")
        for j, call in enumerate(calls):
            if not isinstance(call, dict):
                p.append(f"condition {cid} call {j}: not an object")
                continue
            if not REF_RE.match(str(call.get("ref") or "")):
                p.append(f"condition {cid} call {j}: ref {call.get('ref')!r} is not XXXXXX:Plugin.esp")
            if call.get("target") not in TARGETS:
                p.append(f"condition {cid} call {j}: target must be enabled or disabled")
            if not str(call.get("source") or "").strip():
                p.append(f"condition {cid} call {j}: source (where the call order was read) is required")
    for k, item in enumerate(doc.get("schedule") or []):
        if (not isinstance(item, list) or len(item) != 2 or item[0] not in ids
                or not isinstance(item[1], int) or item[1] < 1):
            p.append(f"schedule[{k}] must be [conditionId, replicate>=1] naming a defined condition")
    sched = schedule_of(doc) if not p else []
    if len(sched) != len(set(map(tuple, sched))):
        p.append("the schedule names a replicate twice")
    return p


def schedule_of(doc) -> list[list]:
    if doc.get("schedule"):
        return [list(x) for x in doc["schedule"]]
    out, conds = [], doc.get("conditions") or []
    for rep in range(1, max((c.get("replicates") or 1) for c in conds) + 1):
        out += [[c["id"], rep] for c in conds if rep <= (c.get("replicates") or 1)]
    return out


def load_plan(path) -> tuple[dict | None, str, list[str]]:
    raw = Path(path).read_bytes()
    try:
        doc = json.loads(raw)
    except ValueError as ex:
        return None, "", [f"not JSON: {ex}"]
    return doc, hashlib.sha256(raw).hexdigest(), validate_plan(doc)


# ------------------------------------------------------------------------------------------- the lock --

def take_lock(lock: Path, what: str) -> None:
    fd = os.open(str(lock), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    try:
        os.write(fd, json.dumps({"owner": "switchflip", "pid": os.getpid(), "taken": utcnow(), "what": what}).encode())
    finally:
        os.close(fd)


def release_lock(lock: Path) -> None:
    try:
        doc = json.loads(lock.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return
    if doc.get("owner") == "switchflip" and doc.get("pid") == os.getpid():
        lock.unlink()


def idle_gate(refuse_if, min_free_gb, log, tries=21, wait_s=30) -> dict | None:
    """Measure until idle (every wait_s, at most tries times). The last verdict is returned either way."""
    v = None
    for i in range(tries):
        v = machineidle.check(min_free_gb=min_free_gb, refuse_if=refuse_if)
        log({"step": "idle", "attempt": i, **{k: v[k] for k in ("idle", "reasons", "freeMB")}})
        if v["idle"]:
            return v
        time.sleep(wait_s)
    return v


# ---------------------------------------------------------------------------------------------- batch --

def launch_job(job_id: str, argv: list[str], cond: dict) -> dict:
    """One launch as a Runner job; Runner features are {name: (value, unit)}."""
    return {"id": job_id, "argv": argv, "features": {"calls": (len(cond.get("calls") or []), "calls"),
                                                      "windowSeconds": (cond.get("windowSeconds"), "seconds")}}


def cmd_batch(a) -> int:
    doc, plan_sha, problems = load_plan(a.plan)
    if problems:
        print("REFUSED: flip plan: " + "; ".join(problems))
        return EXIT_REFUSED_INPUT
    out = Path(a.out)
    if a.dry:
        for cid, rep in schedule_of(doc):
            print(cid, rep, (out / f"{cid}-{rep}").exists() and "(exists)" or "")
        print(json.dumps(machineidle.check(min_free_gb=a.min_free_gb, refuse_if=[a.lock] + a.refuse_if))[:800])
        return 0
    eval_plan = None
    if doc.get("binding"):
        if not a.evalrun_plan:
            print("REFUSED: a bound flip plan needs --evalrun-plan (the plan whose arms it is launched for)")
            return EXIT_REFUSED_INPUT
        eval_plan = json.loads(Path(a.evalrun_plan).read_text(encoding="utf-8"))
        if eval_plan.get("planId") != doc["binding"]["planId"]:
            print(f"REFUSED: --evalrun-plan is plan {eval_plan.get('planId')}, the flip plan is bound to "
                  f"{doc['binding']['planId']}")
            return EXIT_REFUSED_INPUT
    out.mkdir(parents=True, exist_ok=True)
    shutil.copy2(a.plan, out / "flipplan.json")
    lock = Path(a.lock)
    from runlog import Run, Runner
    rows = []
    blog = open(out / "block.jsonl", "a", encoding="utf-8", buffering=1)

    def log(row):
        row = {"t": utcnow(), **row}
        blog.write(json.dumps(row, default=str) + "\n")
        print(json.dumps(row, default=str)[:400], flush=True)

    with Run("switchflip-batch", stage_source="counted") as run:
        sched = schedule_of(doc)
        run.feature("launches", len(sched), "launches")
        run.stage("launches", total=len(sched), unit="launches")
        if lock.exists() or any(Path(x).exists() for x in a.refuse_if):
            log({"step": "refused", "why": "a lock is present", "locks": [str(lock)] + a.refuse_if})
            run.refuse("a lock is present", EXIT_NOT_IDLE)
            return EXIT_NOT_IDLE
        v = idle_gate([str(lock)] + a.refuse_if, a.min_free_gb, log, tries=1, wait_s=0)
        if not v["idle"]:
            run.refuse("machine not idle before the lock: " + "; ".join(v["reasons"])[:300], EXIT_NOT_IDLE)
            return EXIT_NOT_IDLE
        take_lock(lock, f"switchflip block {out.name}: {len(sched)} launch(es)")
        log({"step": "lock", "path": str(lock)})
        rc = 0
        try:
            for cid, rep in sched:
                cond = next(c for c in doc["conditions"] if c["id"] == cid)
                result = None
                for attempt in range(3):          # a launch that never reached the flip: at most two retries
                    folder = out / (f"{cid}-{rep}" if attempt == 0 else f"{cid}-{rep}~{attempt + 1}")
                    if folder.exists():
                        log({"step": "stop", "why": f"{folder} exists: evidence is never overwritten"})
                        return EXIT_STOPPED
                    v = idle_gate(a.refuse_if, a.min_free_gb, log)
                    if not v or not v["idle"]:
                        log({"step": "stop", "why": "the machine did not become idle"})
                        rc = EXIT_NOT_IDLE
                        return rc
                    argv = [sys.executable, str(Path(__file__).resolve()), "launch", "--plan", str(out / "flipplan.json"),
                            "--condition", cid, "--replicate", str(rep), "--folder", str(folder),
                            "--instance", a.instance, "--shortcut", a.shortcut, "--game-exe", a.game_exe,
                            "--port", str(a.port),
                            "--crash-dir", a.crash_dir, "--plan-sha", plan_sha, "--idle-json", json.dumps(v)]
                    if a.evalrun_plan:
                        argv += ["--evalrun-plan", a.evalrun_plan]
                    runner = Runner("switchflip-launch", out_dir=out / "runner",
                                    deadline=time.perf_counter() + a.launch_minutes * 60)
                    res = runner.run([launch_job(f"{cid}-{rep}" + ("" if attempt == 0 else f"~{attempt + 1}"),
                                                 argv, cond)])[0]
                    log({"step": "launch-exit", "folder": folder.name, "exitCode": res.get("exitCode"),
                         "stopped": res.get("stopped")})
                    rpath = folder / "receipt.json"
                    if not rpath.exists():
                        log({"step": "stop", "why": f"{folder.name} wrote no receipt"})
                        return EXIT_STOPPED
                    result = json.loads(rpath.read_text(encoding="utf-8"))
                    if result.get("flipStart") is not None:
                        break
                    log({"step": "retry", "why": "the flip was never reached", "folder": folder.name})
                cls = (result or {}).get("classification") or {}
                rows.append({"condition": cid, "replicate": rep, "folder": folder.name, **cls})
                run.advance()
                exp = cond.get("expect")
                if exp and cls.get("outcome") != exp:
                    log({"step": "stop", "why": f"known-answer block: {cid}-{rep} expected {exp}, measured "
                                                f"{cls.get('outcome')} ({cls.get('why')}); stopped, no retry",
                         "classification": cls})
                    rc = EXIT_STOPPED
                    return rc
            return rc
        finally:
            readings = {}
            for cid in sorted({r["condition"] for r in rows}):
                readings[cid] = O.arm_reading([r for r in rows if r["condition"] == cid])
            (out / "block.json").write_text(json.dumps({"flipPlanSha256": plan_sha, "launches": rows,
                                                        "readings": readings, "exitCode": rc}, indent=1),
                                            encoding="utf-8")
            release_lock(lock)
            log({"step": "unlock", "present": lock.exists()})
            blog.close()


# --------------------------------------------------------------------------------------------- launch --

JOBCHECK_PS = r"""
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class HcJob {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool i, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool IsProcessInJob(IntPtr p, IntPtr j, out bool r);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
}
"@
$p = __PID__
$h = [HcJob]::OpenProcess(0x1000, $false, $p)
if ($h -eq [IntPtr]::Zero) { @{ error = 'OpenProcess ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error() } | ConvertTo-Json; exit }
$r = $false; $ok = [HcJob]::IsProcessInJob($h, [IntPtr]::Zero, [ref]$r); [void][HcJob]::CloseHandle($h)
$pp = Get-CimInstance Win32_Process -Filter "ProcessId=$p"
$par = Get-CimInstance Win32_Process -Filter "ProcessId=$($pp.ParentProcessId)"
@{ name = $pp.Name; inAnyJob = $r; callOk = $ok; parentName = $par.Name } | ConvertTo-Json
"""


def _ps(cmd: str) -> str:
    """Run a PowerShell script passed as -EncodedCommand (UTF-16LE base64), so no quoting layer can alter it."""
    import base64
    enc = base64.b64encode(cmd.encode("utf-16-le")).decode("ascii")
    r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand", enc], capture_output=True,
                       text=True, timeout=120)
    if r.returncode != 0:
        raise RuntimeError(f"powershell failed: {r.stdout} {r.stderr}")
    return r.stdout


def launch_outside_job(shortcut: str) -> dict:
    """Start the MO2 shortcut through WMI (Win32_Process.Create), so the process is not in this shell's job object.

    Why: a CreateProcess that asks to break away from a job without BREAKAWAY_OK fails with ERROR_ACCESS_DENIED, which
    is how MO2 failed to spawn the script extender's loader from an agent's shell while the same shortcut worked from
    the desktop. A process WMI creates is parented by WmiPrvSE and in no job of ours. The created process is checked
    (IsProcessInJob with no job handle = in ANY job); one that is in a job, or not WMI-parented, is terminated by the
    pid this call created and the launch is refused."""
    shortcut = os.path.normpath(shortcut)
    s = json.loads(_ps("$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%s'); "
                       "@{target=$s.TargetPath; args=$s.Arguments; cwd=$s.WorkingDirectory} | ConvertTo-Json -Compress"
                       % shortcut.replace("'", "''")))
    cmdline = '"%s" %s' % (s["target"], s["args"])
    created = json.loads(_ps("$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments "
                             "@{CommandLine='%s'; CurrentDirectory='%s'}; "
                             "@{rv=$r.ReturnValue; pid=$r.ProcessId} | ConvertTo-Json -Compress"
                             % (cmdline.replace("'", "''"), str(Path(s["cwd"])).replace("'", "''"))))
    rec = {"method": "WMI Win32_Process.Create", "commandLine": cmdline, "created": created}
    if created.get("rv") != 0 or not created.get("pid"):
        raise RuntimeError(f"WMI create failed: {created}")
    js = json.loads(_ps(JOBCHECK_PS.replace("__PID__", str(int(created["pid"])))))
    rec["jobCheck"] = js
    if js.get("inAnyJob") is not False or str(js.get("parentName", "")).lower() != "wmiprvse.exe":
        _ps("Get-CimInstance Win32_Process -Filter 'ProcessId=%d' | Invoke-CimMethod -MethodName Terminate"
            % int(created["pid"]))
        raise RuntimeError(f"the created process is in a job or not WMI-parented; terminated: {js}")
    return rec


def measured_configuration(db, a, arm: dict, eval_plan: dict | None, instance: str, profile: str,
                           loaded_names: list[str]) -> tuple[dict, list]:
    """The configuration this launch ran, measured: plugin files hashed (after the window, so hashing never delays the
    flip). Bound: the arm's requested plugins, hashed at the requested paths; standalone: the plugins the running game
    listed right after arrival (`loaded_names`), resolved through the profile."""
    import evalrun
    limits = []
    if eval_plan is not None:
        earm = next(x for x in eval_plan["arms"] if x["armId"] == arm["evalArmId"])
        req = earm.get("requested") or {}
        plugins = []
        for p in req.get("plugins") or []:
            path = p.get("path")
            plugins.append({"name": p["name"], "path": path,
                            "sha256": sha256_file(path) if path and os.path.isfile(path) else None})
        cfg = {"schemaVersion": 1, "source": f"candidate:{earm.get('candidateFile')}", "plugins": plugins,
               "providers": req.get("providers") or [], "overlays": req.get("overlays") or [],
               "population": req.get("population"), "state": "baseline"}
        limits.append("providers and overlays are the plan's request; the live driver measures plugin files and the "
                      "running game's plugin names, not loose-file providers")
    else:
        prof = db._profile_dir(instance, profile)
        roots = db._mod_roots(instance, db._modlist(db._modlist_path(prof)))
        data_dir = os.path.join(os.path.dirname(a.game_exe), "Data")
        plugins = []
        for name in loaded_names:
            path = db._plugin_file(instance, name, roots, data_dir)
            plugins.append({"name": name, "path": path, "sha256": sha256_file(path) if path else None})
        cfg = {"schemaVersion": 1, "source": "standalone", "plugins": plugins, "providers": [], "overlays": [],
               "population": "load-time", "state": "baseline"}
        limits.append("standalone: not bound to an evaluation plan; plugins are the running game's list (regular "
                      "then light), resolved through the profile")
    cfg["fingerprint"] = evalrun.configuration_fingerprint(cfg["plugins"], cfg["providers"], cfg["overlays"],
                                                           cfg["population"], cfg["state"], schema=1)
    cfg["limits"] = limits
    return cfg, limits


def cmd_launch(a) -> int:
    """One launch, as a Runner child of `batch` (which holds the lock). Always writes receipt.json."""
    doc = json.loads(Path(a.plan).read_text(encoding="utf-8"))
    if sha256_file(a.plan) != a.plan_sha:
        print("REFUSED: the flip plan changed since the block started")
        return EXIT_REFUSED_INPUT
    cond = next(c for c in doc["conditions"] if c["id"] == a.condition)
    arm = next(x for x in doc["arms"] if x["id"] == cond["arm"])
    eval_plan = json.loads(Path(a.evalrun_plan).read_text(encoding="utf-8")) if a.evalrun_plan else None
    folder = Path(a.folder)
    folder.mkdir(parents=True, exist_ok=False)
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(folder / "runlog"))
    sys.path.insert(0, str(DBHARNESS_DIR))
    import dbharness as db

    instance, profile = a.instance, arm["profile"]
    lists = ("plugins.txt", "loadorder.txt", "modlist.txt")
    snap = folder / "profile-before"
    logf = open(folder / "log.jsonl", "a", encoding="utf-8", buffering=1)
    events: list[dict] = []
    state = {"t_launch": None, "flip_start": None, "loaded": None}
    receipt = {"schemaVersion": RECEIPT_SCHEMA, "condition": a.condition, "replicate": a.replicate,
               "binding": ({"planId": doc["binding"]["planId"], "checkId": doc["binding"]["checkId"],
                            "armId": arm.get("evalArmId")} if doc.get("binding") else None),
               "arm": arm["id"], "instance": instance, "profile": profile, "shortcut": a.shortcut,
               "flipPlanSha256": a.plan_sha, "windowSeconds": cond["windowSeconds"],
               "settleSeconds": cond.get("settleSeconds", 0), "cell": cond["arrival"]["cell"],
               "calls": cond["calls"], "expect": cond.get("expect"), "idle": json.loads(a.idle_json),
               "tool": {f: sha256_file(HERE / f) for f in ("switchflip.py", "switchflip_outcome.py", "machineidle.py")}
               | {"dbharness.py": sha256_file(DBHARNESS_DIR / "dbharness.py")}}

    def log(**row):
        row["t"] = round(time.time(), 3)
        logf.write(json.dumps(row, default=str) + "\n")
        print(json.dumps(row, default=str)[:300], flush=True)

    def alive():
        x = bool(db._exe_running(os.path.basename(a.game_exe)))
        events.append({"kind": "alive", "exe": x, "t": time.time()})
        return x

    def crash_logs(since):
        found = []
        for p in sorted(Path(a.crash_dir).glob("crash-*.log"), key=lambda q: q.stat().st_mtime) \
                if a.crash_dir and Path(a.crash_dir).is_dir() else []:
            if p.stat().st_mtime > since:
                dst = folder / p.name
                if not dst.exists():
                    shutil.copy2(p, dst)
                found.append({"kind": "crashlog", "path": str(dst), "mtime": p.stat().st_mtime})
        return found

    def timed(kind, name, fn):
        t0 = time.time()
        try:
            r = fn()
        except Exception as e:
            r = {db.CALL_FAILED: {"error": repr(e), "_transport": True}}
        t1 = time.time()
        failed = db.failed(r) or (isinstance(r, dict) and ("error" in r or "_httpError" in r) and not r.get("called"))
        e = {"kind": kind, "name": name, "ok": not failed, "t": t1, "t_send": round(t0, 3),
             "dur_ms": round((t1 - t0) * 1000), "err": json.dumps(r, default=str)[:300] if failed else None, "res": r}
        events.append(e)
        return e

    def window(seconds):
        t_end = time.time() + seconds
        while time.time() < t_end:
            tick = time.time()
            timed("heartbeat", "realtime", lambda: db.ret("Utility", "GetCurrentRealTime", None, None, timeout_ms=1500))
            if not alive():
                break
            if O.classify(events + crash_logs(state["flip_start"]), state["flip_start"])["outcome"] == "fail":
                log(step="classified_early")
                break
            time.sleep(max(0.0, 1.0 - (time.time() - tick)))

    def flip():
        pairs = {c["ref"]: (c["ref"].split(":")[0], c["ref"].split(":", 1)[1]) for c in cond["calls"]}
        fids = db.runtime_formids(pairs)
        log(step="fids", fids=fids)
        state["flip_start"] = time.time()
        receipt["flipStart"] = state["flip_start"]
        db.video_mark("flip")
        for c in cond["calls"]:
            f, want_disabled = fids[c["ref"]], c["target"] == "disabled"
            before = db.ret("ObjectReference", "IsDisabled", f)
            if db.failed(before) or before is None:
                events.append({"kind": "readback", "name": c["ref"] + ":before", "ok": False, "t": time.time(),
                               "err": json.dumps(before, default=str)[:300]})
                if not alive():
                    break
                continue
            if bool(before) == want_disabled:
                events.append({"kind": "skipped", "name": c["ref"], "t": time.time(),
                               "why": "no change from its load-time state: already " + c["target"]})
                log(step="skipped", ref=c["ref"])
                continue
            call = timed("call", c["ref"], lambda: db.papyrus("ObjectReference",
                                                              "Disable" if want_disabled else "Enable", f))
            time.sleep(CALL_SPACING_S)
            back = timed("readback", c["ref"], lambda: db.ret("ObjectReference", "IsDisabled", f))
            back["target_reached"] = bool(back["ok"] and back["res"] is want_disabled)
            log(step="call", ref=c["ref"], before=before, ok=call["ok"], ms=call["dur_ms"], back=back["res"])
            if not alive():
                break

    mo2_pid, video = None, False
    try:
        if db._exe_running(*db.MO2_EXES):
            raise RuntimeError(f"refusing: {db._exe_running(*db.MO2_EXES)} running")
        snap.mkdir()
        for f in lists:
            shutil.copy2(os.path.join(instance, "profiles", profile, f), snap / f)
        shutil.copy2(os.path.join(instance, "ModOrganizer.ini"), snap / "ModOrganizer.ini")
        log(step="select", res=db.select_profile(instance, profile))
        pre = db.popup_preflight(instance, profile)
        log(step="popup_preflight", ok=pre["ok"], missing=pre["missing"], warnings=pre["warnings"])
        if not pre["ok"]:
            raise RuntimeError("the profile is not quiet (popup preflight)")
        db.configure(port=a.port, shot_dir=str(folder / "shots"), recording="rig")
        state["t_launch"] = time.time()
        receipt["launchStartedUtc"] = utcnow()
        rec = launch_outside_job(a.shortcut)
        mo2_pid = int(rec["created"]["pid"])
        log(step="launch", record=rec)
        h = db.wait_ready(limit=420)
        log(step="ready", seconds=round(time.time() - state["t_launch"], 1), health=h)
        db.health_check(expect_exe=os.path.splitext(os.path.basename(a.game_exe))[0])
        try:
            v = db.video_start(str(folder), label=f"switchflip-{a.condition}-{a.replicate}", hook_limit=6,
                               allow_display=False)
        except Exception as e:      # the first start can find no frame; one retry
            log(step="video_start_retry", error=repr(e))
            try:
                db.video_stop()
            except Exception as e2:
                log(step="video_stop_before_retry", error=repr(e2))
            time.sleep(15)
            v = db.video_start(str(folder), label=f"switchflip-{a.condition}-{a.replicate}", hook_limit=20,
                               allow_display=False)
        video = True
        log(step="video_start", mode=v.get("mode"))
        if v.get("mode") != "game":
            raise RuntimeError(f"video mode {v.get('mode')}: game capture is required")
        if db.loaded() is not None:
            raise RuntimeError(f"a game is already loaded ({db.loaded()})")
        db.video_mark(f"arrive {cond['arrival']['cell']}")
        ral = db.read_at_load(cond["arrival"]["cell"], {}, schedule=(0,))
        log(step="read_at_load", cell=ral.get("cell"), fresh=ral.get("fresh_game"))
        ok, hours = db.unpause(strict=False)
        db.protect_body(log=None, note="switchflip: the body is not the subject")
        log(step="arrived", cell=db.loaded(), unpause=ok)
        state["loaded"] = db.loaded_plugins()
        log(step="loaded_plugins", count=len(state["loaded"]))
        if eval_plan is not None:
            earm = next(x for x in eval_plan["arms"] if x["armId"] == arm["evalArmId"])
            want = sorted(str(p["name"]).lower() for p in (earm.get("requested") or {}).get("plugins") or [])
            got = sorted(n.lower() for n in state["loaded"])
            if want != got:
                raise RuntimeError("the running game's plugins differ from the arm's request: missing "
                                   f"{sorted(set(want) - set(got))[:8]}, extra {sorted(set(got) - set(want))[:8]}")
        if cond.get("settleSeconds"):
            db.video_mark(f"settle {cond['settleSeconds']}s")
            time.sleep(cond["settleSeconds"])
        flip()
        if alive():
            db.video_mark("window")
            window(cond["windowSeconds"])
    except Exception as e:
        log(step="abort", error=repr(e), tb=traceback.format_exc())
        receipt["abort"] = repr(e)[:500]
    finally:
        time.sleep(3.0 if state["flip_start"] else 0)       # let a crash logger finish writing
        crashes = crash_logs(state["flip_start"] or state["t_launch"] or time.time())
        alive()
        all_events = events + crashes
        receipt.setdefault("flipStart", state["flip_start"])
        cls = (O.classify(all_events, state["flip_start"], cond["windowSeconds"]) if state["flip_start"] else
               {"outcome": "not-measured", "branch": None,
                "why": "the flip was never started" + (f": {receipt.get('abort')}" if receipt.get("abort") else "")})
        receipt["classification"] = cls
        log(step="classified", **cls)
        if (cond.get("canaryAfter") and state["flip_start"] and cls["outcome"] == "complete" and alive()):
            try:
                # A detector check, never a mod finding: the known crash, through the same classifier.
                canary_events, t_c = [], time.time()
                events.clear()
                db.video_mark("canary")
                timed("call", "canary", lambda: db.post("console", {"action": "exec",
                                                                   "command": doc["canary"]["consoleCommand"]}, timeout=30))
                t_end = time.time() + 30
                while time.time() < t_end and alive():
                    timed("heartbeat", "realtime", lambda: db.ret("Utility", "GetCurrentRealTime", None, None,
                                                                  timeout_ms=1500))
                    time.sleep(1.0)
                time.sleep(3.0)
                alive()
                canary_events = events + crash_logs(t_c)
                receipt["canary"] = {"label": doc["canary"].get("label") or "detector check (a known crash)",
                                     "flipStart": t_c, "events": [{k: v for k, v in e.items() if k != "res"}
                                                                  for e in canary_events],
                                     "classification": O.classify(canary_events, t_c, 0.0)}
                log(step="canary", **receipt["canary"]["classification"])
            except Exception as e:
                receipt["canaryError"] = repr(e)[:300]
                log(step="canary_error", error=repr(e))
        if state.get("loaded") is not None:
            try:
                receipt["configuration"], _lim = measured_configuration(db, a, arm, eval_plan, instance, profile,
                                                                        state["loaded"])
                if any(p["sha256"] is None for p in receipt["configuration"]["plugins"]):
                    receipt["configurationProblem"] = "a plugin file could not be found on disk to hash"
            except Exception as e:
                receipt["configurationProblem"] = repr(e)[:300]
        ev_path = folder / "events.jsonl"
        ev_path.write_text("".join(json.dumps({k: v for k, v in e.items() if k != "res"}, default=str) + "\n"
                                   for e in all_events), encoding="utf-8")
        receipt["eventsSha256"] = sha256_file(ev_path)
        receipt["crashLogs"] = [{"path": Path(c["path"]).name, "sha256": sha256_file(c["path"])}
                                for c in crashes + [x for x in (receipt.get("canary") or {}).get("events") or []
                                                    if x.get("kind") == "crashlog"]]
        if video:
            try:
                if alive():
                    db.video_mark("end")
                receipt["recording"] = db.video_stop()
                log(step="video_stop", res=receipt["recording"])
            except Exception as e:
                log(step="video_stop_error", error=repr(e))
        try:
            log(step="teardown", res=db.teardown())
        except Exception as e:
            log(step="teardown_error", error=repr(e))
        if mo2_pid:
            try:
                _ps(f"$p = Get-Process -Id {mo2_pid} -ErrorAction SilentlyContinue; "
                    f"if ($p) {{ $p.CloseMainWindow() | Out-Null }}")
                t0 = time.time()
                while time.time() - t0 < 180 and mo2_pid in db._pids_of("ModOrganizer.exe"):
                    time.sleep(3)
                log(step="mo2_closed", ok=mo2_pid not in db._pids_of("ModOrganizer.exe"))
            except Exception as e:
                log(step="mo2_close_error", error=repr(e))
        try:
            log(step="unselect", res=db.select_profile(instance, restore=True))
        except Exception as e:
            log(step="unselect_error", error=repr(e))
        proof = {"running": db._exe_running(*db.MO2_EXES), "restored": []}
        try:
            if (snap / "ModOrganizer.ini").exists():
                proof["mo2IniIdentical"] = (Path(instance, "ModOrganizer.ini").read_bytes()
                                            == (snap / "ModOrganizer.ini").read_bytes())
            for f in lists:
                cur, ref = Path(instance, "profiles", profile, f), snap / f
                if ref.exists() and cur.read_bytes() != ref.read_bytes() and not proof["running"]:
                    shutil.copy2(cur, folder / f"{f}.as-left")
                    shutil.copy2(ref, cur)
                    proof["restored"].append(f)
        except Exception as e:
            proof["error"] = repr(e)[:300]
        receipt["restoreProof"] = proof
        log(step="proof", **proof)
        try:
            receipt["obsClose"] = db.obs_close()
        except Exception as e:
            log(step="obs_close_error", error=repr(e))
        (folder / "receipt.json").write_text(json.dumps(receipt, indent=1, default=str), encoding="utf-8")
        log(step="done")
        logf.close()
    return 0


# --------------------------------------------------------------------------------------------- export --

def cmd_export(a) -> int:
    """Assemble one arm's import document for `evalrun import-results` from a block's launch folders."""
    block = Path(a.block)
    doc = json.loads((block / "flipplan.json").read_text(encoding="utf-8"))
    if not doc.get("binding"):
        print("REFUSED: a standalone block is not bound to an evaluation plan and cannot be imported")
        return EXIT_REFUSED_INPUT
    arm = next((x for x in doc["arms"] if x["id"] == a.arm), None)
    if arm is None:
        print(f"REFUSED: the flip plan has no arm {a.arm!r}")
        return EXIT_REFUSED_INPUT
    conds = {c["id"] for c in doc["conditions"] if c["arm"] == a.arm}
    outputs = [{"role": "flip-plan", "path": str((block / "flipplan.json").resolve()),
                "sha256": sha256_file(block / "flipplan.json")}]
    launches, cfg = [], None
    for folder in sorted(p for p in block.iterdir() if p.is_dir() and (p / "receipt.json").exists()):
        rc = json.loads((folder / "receipt.json").read_text(encoding="utf-8"))
        if rc.get("condition") not in conds:
            continue
        launches.append(rc)
        cfg = cfg or rc.get("configuration")
        outputs += [{"role": "launch-receipt", "path": str((folder / "receipt.json").resolve()),
                     "sha256": sha256_file(folder / "receipt.json")},
                    {"role": "events", "path": str((folder / "events.jsonl").resolve()),
                     "sha256": sha256_file(folder / "events.jsonl")}]
        for c in rc.get("crashLogs") or []:
            outputs.append({"role": "crash-log", "path": str((folder / c["path"]).resolve()), "sha256": c["sha256"]})
    readings = {}
    for cid in sorted(conds):
        readings[cid] = O.arm_reading([r["classification"] for r in launches if r["condition"] == cid])
    findings = sum(1 for r in readings.values() if r["reading"] == "finding")
    out = {"schemaVersion": 1, "planId": doc["binding"]["planId"], "checkId": doc["binding"]["checkId"],
           "armId": arm["evalArmId"], "capturedUtc": utcnow(),
           "tool": {"name": "switchflip", "sha256": sha256_file(Path(__file__))}, "state": "baseline",
           "configuration": cfg,
           "result": {"detector": "finding" if findings else "no-finding", "findings": findings,
                      "coverage": [{"Population": f"switch-flip launches: {c}",
                                    "Judged": r["failed"] + r["completed"], "Unjudgeable": r["unjudgeable"]}
                                   for c, r in readings.items()]},
           "readings": readings, "outputs": outputs}
    Path(a.out).write_text(json.dumps(out, indent=1), encoding="utf-8")
    unsettled = [c for c, r in readings.items() if r["reading"] == "unsettled"]
    print(f"wrote {a.out}: {len(launches)} launch(es); " + (f"UNSETTLED {unsettled}: the import will be refused"
                                                            if unsettled else f"{findings} condition(s) with a finding"))
    return 0


def cmd_validate(a) -> int:
    doc, sha, problems = load_plan(a.plan)
    if problems:
        print("REFUSED: " + "; ".join(problems))
        return EXIT_REFUSED_INPUT
    print(f"valid flip plan {sha[:12]}: {len(doc['conditions'])} condition(s), {len(schedule_of(doc))} launch(es)")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="the live driver of the runtime state-switch check")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("validate", help="check a flip plan; launches nothing")
    p.add_argument("--plan", required=True)
    p.set_defaults(fn=cmd_validate)
    crash_default = os.path.join(os.path.expanduser("~"), "Documents", "My Games", "Skyrim Special Edition", "SKSE")
    for name in ("batch", "launch"):
        p = sub.add_parser(name, help=("run a block of launches under one lock" if name == "batch"
                                       else "one launch (a child of batch; never run alone)"))
        p.add_argument("--plan", required=True, help="the flip plan JSON")
        p.add_argument("--instance", required=True, help="the MO2 instance folder (a scratch instance)")
        p.add_argument("--shortcut", required=True, help="the instance's launch shortcut (.lnk)")
        p.add_argument("--game-exe", required=True, help="the game executable's full path (always explicit; its "
                                                         "folder's Data resolves base plugins)")
        p.add_argument("--port", type=int, default=8920, help="the DevBench port")
        p.add_argument("--crash-dir", default=crash_default, help="where the crash logger writes crash-*.log")
        p.add_argument("--evalrun-plan", help="the evaluation plan.json a bound flip plan is launched for")
        if name == "batch":
            p.add_argument("--out", required=True, help="the block folder (one subfolder per launch)")
            p.add_argument("--lock", required=True, help="the lock file held for the whole block (created only "
                                                         "when absent)")
            p.add_argument("--refuse-if", action="append", default=[], help="another lock whose presence refuses")
            p.add_argument("--min-free-gb", type=float, default=machineidle.DEFAULT_MIN_FREE_GB)
            p.add_argument("--launch-minutes", type=float, default=25.0, help="the deadline of one launch")
            p.add_argument("--dry", action="store_true", help="print the schedule and the idle verdict; launch nothing")
            p.set_defaults(fn=cmd_batch)
        else:
            p.add_argument("--condition", required=True)
            p.add_argument("--replicate", type=int, required=True)
            p.add_argument("--folder", required=True)
            p.add_argument("--plan-sha", required=True)
            p.add_argument("--idle-json", required=True)
            p.set_defaults(fn=cmd_launch)
    p = sub.add_parser("export", help="assemble one arm's import document for evalrun import-results")
    p.add_argument("--block", required=True)
    p.add_argument("--arm", required=True)
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_export)
    a = ap.parse_args(argv)
    if a.cmd in ("validate", "export"):
        from runlog import Run
        with Run(f"switchflip-{a.cmd}", stage_source="counted") as run:
            run.stage(a.cmd, total=1, unit="files")
            rc = a.fn(a)
            if rc == EXIT_REFUSED_INPUT:
                run.refuse(f"{a.cmd} refused; the diagnostic above names the cause", rc)
            run.advance()
            return rc
    if a.cmd == "launch":
        from runlog import Run
        with Run("switchflip-launch-body", stage_source="external-opaque") as run:
            run.stage("launch", total=1, unit="launches")
            rc = a.fn(a)
            run.advance()
            return rc
    return a.fn(a)


if __name__ == "__main__":
    sys.exit(main())
