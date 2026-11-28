"""Known answers for the idle gate (machineidle.py). Synthetic snapshots; `decide()` is pure.

Dependency-free: `python _geometry-harness/tests/test_machineidle.py`.
Each test names the wrong answer it rejects.
"""

import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
sys.path.insert(0, os.path.dirname(HERE))

import machineidle as M  # noqa: E402

FAILURES, PASSED = [], []


def check(name, fn):
    try:
        fn()
    except Exception:
        FAILURES.append(name)
        print(f"FAIL  {name}")
        print("      " + traceback.format_exc().strip().replace("\n", "\n      "))
    else:
        PASSED.append(name)
        print(f"ok    {name}")


SELF, PARENT = 500, 400


def snap(*procs, free_mb=40000):
    base = [{"name": "python.exe", "pid": SELF, "ppid": PARENT, "cmd": "python switchflip.py batch"},
            {"name": "bash.exe", "pid": PARENT, "ppid": 1, "cmd": "bash -c python switchflip.py"},
            {"name": "explorer.exe", "pid": 10, "ppid": 1, "cmd": "C:\\Windows\\explorer.exe"}]
    rows = [dict(p, ppid=p.get("ppid", 1)) for p in procs]
    return {"freeMB": free_mb, "processes": base + rows, "self": SELF}


def no_lock(_):
    return False


def test_quiet_machine_is_idle():
    v = M.decide(snap(), exists=no_lock)
    assert v["idle"], v


# K9 -- rejects: passing a probe run as `dotnet <probe>.dll` (the old name-only gate exempted small dotnet hosts)
def test_k9_probe_found_by_command_line_under_dotnet():
    v = M.decide(snap({"name": "dotnet.exe", "pid": 77, "wsMB": 200,
                       "cmd": '"C:\\Program Files\\dotnet\\dotnet.exe" X:\\h\\contactprobe\\bin\\contactprobe.dll --pairs a'}),
                 exists=no_lock)
    assert not v["idle"], v
    assert "by command line" in v["reasons"][0] and "contactprobe" in v["reasons"][0], v


def test_k9b_unlisted_probe_exe_found_by_file_name():
    v = M.decide(snap({"name": "newthingprobe.exe", "pid": 78, "cmd": "D:\\x\\newthingprobe.exe --json o"}),
                 exists=no_lock)
    assert not v["idle"] and "newthingprobe" in v["reasons"][0], v


def test_k9c_conflictdiff_by_command_line():
    v = M.decide(snap({"name": "dotnet.exe", "pid": 79, "cmd": "dotnet conflictdiff.dll a b"}), exists=no_lock)
    assert not v["idle"], v


# K10 -- rejects: refusing an idle IDE server whose command line merely contains "msbuild"
def test_k10_language_server_with_msbuild_in_command_line_is_not_refused():
    cmd = ('"c:\\Users\\u\\.vscode\\extensions\\ms-dotnettools.csdevkit\\server\\'
           'Microsoft.VisualStudio.Code.ServiceHost.exe" --pipe x --msbuildPath C:\\Program Files\\dotnet\\sdk\\MSBuild')
    v = M.decide(snap({"name": "Microsoft.VisualStudio.Code.ServiceHost.exe", "pid": 80, "cmd": cmd},
                      {"name": "dotnet.exe", "pid": 81,
                       "cmd": 'dotnet "c:\\x\\Microsoft.CodeAnalysis.LanguageServer.dll" --msbuild-hosted'}),
                 exists=no_lock)
    assert v["idle"], v


# K11 -- rejects: passing any named blocker, low memory or a present lock
def test_k11_blockers_by_name():
    for name in ("MSBuild.exe", "VBCSCompiler.exe", "SkyrimSE.exe", "ModOrganizer.exe", "obs64.exe",
                 "skse64_loader.exe"):
        v = M.decide(snap({"name": name, "pid": 90, "cmd": ""}), exists=no_lock)
        assert not v["idle"] and "by process name" in v["reasons"][0], (name, v)


def test_k11_dotnet_build_host_refused():
    for cmd in ("dotnet build x.sln", '"C:\\Program Files\\dotnet\\dotnet.exe" "C:\\Program Files\\dotnet\\sdk\\9.0\\MSBuild.dll" -nodeReuse'):
        v = M.decide(snap({"name": "dotnet.exe", "pid": 91, "cmd": cmd}), exists=no_lock)
        assert not v["idle"], (cmd, v)


def test_k11_low_memory_refused():
    v = M.decide(snap(free_mb=15 * 1024), exists=no_lock)
    assert not v["idle"] and "below 16 GB" in v["reasons"][0], v


def test_k11_lock_present_refused():
    v = M.decide(snap(), refuse_if=["X/HEAVY-LOCK.json"], exists=lambda p: p.endswith("HEAVY-LOCK.json"))
    assert not v["idle"] and "lock present" in v["reasons"][0], v


def test_own_process_and_ancestors_not_counted():
    s = snap()
    s["processes"][1]["cmd"] = "bash -c 'python switchflip.py --note contactprobe'"
    v = M.decide(s, exists=no_lock)
    assert v["idle"], v


# ---- unavailable snapshots (task 2026-10-06-machineidle-unavailable-snapshot) -----------------------------------
# The snapshot runs PowerShell CIM. A CIM failure is a NONTERMINATING error: PowerShell still exits 0 and prints JSON in
# which the unread values became 0 / an empty list. The unchanged reader turned that into "free physical memory 0 MB is
# below the floor" (exit 3), or, with only the process list missing, into IDLE. A snapshot that could not be read must
# refuse by name with the documented exit 2 before any decision. These drive main() end to end with the PowerShell
# call replaced, except U5, which runs the real PowerShell with an unreadable class.

import contextlib  # noqa: E402
import io  # noqa: E402
import json  # noqa: E402
import subprocess  # noqa: E402
import tempfile  # noqa: E402

ACCESS_DENIED = ("Get-CimInstance : Access denied \r\nAt line:1 char:4\r\n"
                 "    + CategoryInfo          : PermissionDenied: (root\\cimv2:Win32_OperatingSystem:String) "
                 "[Get-CimInstance], CimException\r\n"
                 "    + FullyQualifiedErrorId : HRESULT 0x80041003,Microsoft.Management.Infrastructure.CimCmdlets."
                 "GetCimInstanceCommand\r\n")
PROCS = [{"Name": "System", "ProcessId": 4, "ParentProcessId": 0, "CommandLine": None, "WorkingSetSize": 1},
         {"Name": "explorer.exe", "ProcessId": 10, "ParentProcessId": 1, "CommandLine": "C:\\Windows\\explorer.exe",
          "WorkingSetSize": 2 ** 27}]


def run_main(stdout, returncode=0, stderr="", argv=()):
    """main() with the PowerShell call answered by (returncode, stdout, stderr); returns (exit, printed text)."""
    real_run, seen = M.subprocess.run, []

    def fake(cmd, **kw):
        seen.append(cmd)
        return subprocess.CompletedProcess(cmd, returncode, stdout, stderr)
    M.subprocess.run = fake
    out = io.StringIO()
    try:
        with tempfile.TemporaryDirectory() as tmp:
            _runlog = SW.set_run_log(tmp)
            with contextlib.redirect_stdout(out):
                code = M.main(list(argv))
    finally:
        M.subprocess.run = real_run
        SW.restore_run_log(_runlog)
    assert seen and seen[0][0] == "powershell", seen
    return code, out.getvalue()


def ps_json(free_kb, procs):
    doc = {"processes": procs}
    if free_kb is not ...:
        doc["freeKB"] = free_kb
    return json.dumps(doc)


# U1 -- rejects: the retained failure (access denied, exit 0, zeros) reported as low memory, exit 3
def test_u1_access_denied_zero_snapshot_refused_as_unavailable():
    code, out = run_main(ps_json(0, []), stderr=ACCESS_DENIED)
    assert code == 2, (code, out)
    assert "unavailable" in out.lower() and "below" not in out, out
    assert "Access denied" in out, out


# U2 -- rejects: the memory reading alone failing (processes read) reported as low memory, exit 3
def test_u2_memory_unread_refused_as_unavailable():
    code, out = run_main(ps_json(0, PROCS), stderr=ACCESS_DENIED)
    assert code == 2 and "unavailable" in out.lower() and "below" not in out, (code, out)


# U3 -- rejects: the process list failing (memory read) passed as IDLE, exit 0
def test_u3_process_list_unread_refused_as_unavailable():
    code, out = run_main(ps_json(40 * 2 ** 20, []), stderr=ACCESS_DENIED.replace("Win32_OperatingSystem", "Win32_Process"))
    assert code == 2 and "unavailable" in out.lower() and "IDLE" not in out.replace("NOT IDLE", ""), (code, out)


# U4 -- rejects: a snapshot with no memory field at all reported as low memory
def test_u4_memory_field_missing_refused_as_unavailable():
    code, out = run_main(ps_json(..., PROCS))
    assert code == 2 and "unavailable" in out.lower() and "below" not in out, (code, out)


# U5 -- rejects: the REAL PowerShell's nonterminating CIM error (an unreadable class) reported as low memory, exit 3.
# One light CIM query; the class name is the only change to the shipped script.
def test_u5_real_powershell_nonterminating_error_refused_as_unavailable():
    assert "Win32_OperatingSystem" in M.PS_SNAPSHOT, M.PS_SNAPSHOT
    real_script = M.PS_SNAPSHOT
    M.PS_SNAPSHOT = real_script.replace("Win32_OperatingSystem", "Win32_NoSuchClassMachineIdleTest")
    out = io.StringIO()
    try:
        with tempfile.TemporaryDirectory() as tmp:
            _runlog = SW.set_run_log(tmp)
            with contextlib.redirect_stdout(out):
                code = M.main([])
    finally:
        M.PS_SNAPSHOT = real_script
        SW.restore_run_log(_runlog)
    text = out.getvalue()
    assert code == 2 and "unavailable" in text.lower() and "below" not in text, (code, text)


# U6 -- rejects: an unreadable snapshot leaving no --json (a stale idle.json from an earlier run survived), and a
# refusal that does not say which reading failed
def test_u6_unavailable_record_written_and_reading_named():
    with tempfile.TemporaryDirectory() as tmp:
        out_json = os.path.join(tmp, "idle.json")
        with open(out_json, "w", encoding="utf-8") as f:
            json.dump({"idle": True, "freeMB": 40000}, f)  # a stale verdict from an earlier run
        code, out = run_main(ps_json(40 * 2 ** 20, []), argv=["--json", out_json])
        with open(out_json, encoding="utf-8") as f:
            doc = json.load(f)
    assert code == 2 and "process list" in out, (code, out)
    assert doc["idle"] is False and "process list" in doc["unavailable"] and doc["freeMB"] is None, doc
    code, out = run_main(ps_json(0, PROCS), stderr=ACCESS_DENIED)
    assert code == 2 and "free physical memory" in out and "Access denied" in out, (code, out)


# U7 -- rejects: switchflip's per-launch gate reading an unreadable snapshot as idle. It calls check(), which now
# raises; the gate must stop (raise), never return an idle verdict.
def test_u7_switchflip_gate_never_idle_on_unavailable_snapshot():
    import switchflip  # noqa: E402  (imports this module as `machineidle`)
    real_run, real_sleep = switchflip.machineidle.subprocess.run, switchflip.time.sleep
    switchflip.machineidle.subprocess.run = lambda cmd, **kw: subprocess.CompletedProcess(
        cmd, 0, ps_json(0, []), ACCESS_DENIED)
    switchflip.time.sleep = lambda s: None
    logged = []
    try:
        try:
            v = switchflip.idle_gate([], 16.0, logged.append, tries=2, wait_s=0)
        except M.SnapshotUnavailable as ex:
            v = ex
    finally:
        switchflip.machineidle.subprocess.run, switchflip.time.sleep = real_run, real_sleep
    assert isinstance(v, M.SnapshotUnavailable), v
    assert not any(e.get("idle") for e in logged), logged


# P1..P4 -- kept: a readable snapshot still decides exactly as before; a failed PowerShell still refuses with exit 2
def test_p1_readable_low_memory_still_exit_3():
    code, out = run_main(ps_json(15 * 2 ** 20, PROCS))
    assert code == 3 and "below 16 GB" in out, (code, out)


def test_p2_readable_dotnet_hosted_probe_still_exit_3():
    procs = PROCS + [{"Name": "dotnet.exe", "ProcessId": 77, "ParentProcessId": 1, "WorkingSetSize": 2 ** 28,
                      "CommandLine": "dotnet X:\\h\\contactprobe\\bin\\contactprobe.dll --pairs a"}]
    code, out = run_main(ps_json(40 * 2 ** 20, procs))
    assert code == 3 and "by command line" in out and "contactprobe" in out, (code, out)


def test_p3_readable_quiet_machine_still_idle():
    code, out = run_main(ps_json(40 * 2 ** 20, PROCS))
    assert code == 0 and out.startswith("IDLE"), (code, out)


def test_p4_powershell_failure_still_exit_2():
    for rc, so in ((1, ""), (0, ""), (0, "not json")):
        code, out = run_main(so, returncode=rc, stderr=ACCESS_DENIED)
        assert code == 2 and "REFUSED" in out, (rc, so, code, out)


# ---- a probe actually running, not a command line that names one (task 2026-10-09-tooling-follow-ups item 1) -------
# The command-line rule matched a probe's name anywhere in any process's command line, so a shell that hashed, listed or
# edited a probe's file read as "a probe running". The 2026-10-09 review's live run listed two bash.exe that way. A probe
# is running when a process IS its executable, or a dotnet host's program argument is its dll.

SHELL_WORDS = "a shell or script naming a probe (may be a batch driver)"


# M1 -- kept conservative (Gate A+B ruling): a shell that is not the caller's ancestor and names a probe may be a batch
# driver between two probe children, so it still blocks; rejects calling it "a probe running"
def test_m1_shell_naming_a_probe_blocks_as_a_shell_not_a_probe():
    v = M.decide(snap({"name": "bash.exe", "pid": 120, "ppid": 1,
                       "cmd": '"C:\\Program Files\\Git\\bin\\bash.exe" -c "sha256sum '
                              'X:/h/switchprobe/bin/Release/net9.0/switchprobe.dll"'}),
                 exists=no_lock)
    assert not v["idle"] and SHELL_WORDS in v["reasons"][0] and "switchprobe" in v["reasons"][0], v
    assert "a probe running" not in v["reasons"][0], v


# M2 -- kept conservative: the same for PowerShell (and every listed shell or interpreter)
def test_m2_shells_and_interpreters_naming_a_probe_block_as_shells():
    for name, cmd in (("powershell.exe", "powershell -NoProfile -Command Get-FileHash X:\\h\\contactprobe\\bin\\contactprobe.exe"),
                      ("pwsh.exe", "pwsh -File drive.ps1 -Probe overlapprobe"),
                      ("cmd.exe", "cmd /c for %p in (a b) do contactprobe.exe %p"),
                      ("sh.exe", "sh -c 'viewprobe.exe --x'"),
                      ("python.exe", "python ordersweep.py --select contactprobe"),
                      ("node.exe", "node drive.js navprobe")):
        v = M.decide(snap({"name": name, "pid": 121, "ppid": 1, "cmd": cmd}), exists=no_lock)
        assert not v["idle"] and SHELL_WORDS in v["reasons"][0], (name, v)
        assert "a probe running" not in v["reasons"][0], (name, v)


# M3 -- rejects: an editor holding a file in a probe's source folder listed as a probe running
def test_m3_editor_on_a_probe_source_file_is_not_a_probe():
    v = M.decide(snap({"name": "Code.exe", "pid": 122, "ppid": 1,
                       "cmd": '"C:\\Users\\u\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe" '
                              'S:\\Dev\\houseCARL\\_geometry-harness\\viewprobe\\Program.cs'}),
                 exists=no_lock)
    assert v["idle"], v


# M4 -- rejects: a dotnet host running another program whose ARGUMENTS name a probe listed as a probe running
def test_m4_dotnet_host_of_another_dll_naming_a_probe_is_not_a_probe():
    v = M.decide(snap({"name": "dotnet.exe", "pid": 123, "ppid": 1,
                       "cmd": "dotnet S:\\x\\housecarl-runlog.dll history --op contactprobe"}),
                 exists=no_lock)
    assert v["idle"], v


# M5 -- rejects: passing a probe executable whose command line could not be read (CIM gives null for some processes)
def test_m5_probe_exe_with_unreadable_command_line_still_blocks():
    v = M.decide(snap({"name": "overlapprobe.exe", "pid": 124, "ppid": 1, "cmd": ""}), exists=no_lock)
    assert not v["idle"] and "overlapprobe" in v["reasons"][0], v


# Q1..Q6 -- kept: every way a probe actually runs still blocks
def test_q1_dotnet_exec_with_options_still_blocks():
    v = M.decide(snap({"name": "dotnet.exe", "pid": 130, "ppid": 1,
                       "cmd": "dotnet exec --runtimeconfig X:\\h\\overlapprobe.runtimeconfig.json "
                              "X:\\h\\overlapprobe\\bin\\overlapprobe.dll --order o.json"}), exists=no_lock)
    assert not v["idle"] and "overlapprobe" in v["reasons"][0], v


def test_q2_dotnet_quoted_path_with_spaces_still_blocks():
    v = M.decide(snap({"name": "dotnet.exe", "pid": 131, "ppid": 1,
                       "cmd": '"C:\\Program Files\\dotnet\\dotnet.exe" "S:\\my dir\\contactprobe\\bin\\contactprobe.dll" '
                              "--pairs a"}), exists=no_lock)
    assert not v["idle"] and "contactprobe" in v["reasons"][0], v


def test_q3_listed_probe_apphost_by_path_still_blocks():
    v = M.decide(snap({"name": "ContactProbe.EXE", "pid": 132, "ppid": 1,
                       "cmd": '"X:\\h\\contactprobe\\bin\\ContactProbe.EXE" --pairs a'}), exists=no_lock)
    assert not v["idle"] and "contactprobe" in v["reasons"][0].lower(), v


def test_q4_unlisted_probe_dll_under_dotnet_still_blocks():
    v = M.decide(snap({"name": "dotnet.exe", "pid": 133, "ppid": 1, "cmd": "dotnet D:\\x\\newthingprobe.dll --json o"}),
                 exists=no_lock)
    assert not v["idle"] and "newthingprobe" in v["reasons"][0], v


def test_q5_conflictdiff_exe_by_name_still_blocks():
    v = M.decide(snap({"name": "conflictdiff.exe", "pid": 134, "ppid": 1, "cmd": "conflictdiff.exe a.json b.json"}),
                 exists=no_lock)
    assert not v["idle"] and "conflictdiff" in v["reasons"][0], v


def test_q6_dotnet_run_of_a_probe_project_still_blocks_as_a_build():
    v = M.decide(snap({"name": "dotnet.exe", "pid": 135, "ppid": 1,
                       "cmd": "dotnet run --project _geometry-harness/contactprobe -c Release"}), exists=no_lock)
    assert not v["idle"], v


# ---- one rule for a hold-admitted caller (task 2026-11-08-idle-lock-keep-held-job-output, ruling D4 (C)) -----------
# Standalone, a probe-naming shell still blocks (M1/M2). A caller an idle_lock hold admitted (its job environment names
# a slot file whose token matches and whose holder runs) leaves such a shell to that hold's own check: noted, not
# counted. Item 35's smoke (2026-11-09) was refused inside its hold by 18 leftover shells the hold had already noted.

SHELL_ROW = {"name": "bash.exe", "pid": 120, "ppid": 1,
             "cmd": '"C:\\Program Files\\Git\\bin\\bash.exe" -c "sha256sum X:/h/switchprobe/bin/Release/net9.0/switchprobe.dll"'}


def _slot_env(tmp, token="tok", pid=None, file_token=None):
    import json
    p = os.path.join(tmp, "HEAVY-SLOT")
    with open(p, "w", encoding="utf-8") as fh:
        json.dump({"pid": os.getpid() if pid is None else pid, "token": file_token or token, "purpose": "ka hold"}, fh)
    return {"IDLE_LOCK_SLOT": token, "IDLE_LOCK_SLOT_FILE": p}


# C1826 -- rejects a hold-admitted caller refusing on a shell its admitting hold already noted
def test_c1826_hold_admitted_caller_notes_a_probe_naming_shell():
    import tempfile
    v = M.decide(snap(SHELL_ROW), exists=no_lock, count_shells=False)
    assert v["idle"] and any("pid 120" in n for n in v["noted"]), v
    with tempfile.TemporaryDirectory() as d:
        held = M.admitting_hold(_slot_env(d))
        assert held and str(os.getpid()) in held, held
        import switchflip
        saved_env = {k: os.environ.get(k) for k in ("IDLE_LOCK_SLOT", "IDLE_LOCK_SLOT_FILE")}
        saved_snap = M.snapshot
        os.environ.update(_slot_env(d))
        M.snapshot = lambda: snap(SHELL_ROW)
        rows = []
        try:
            g = switchflip.idle_gate([], 16.0, rows.append, tries=1, wait_s=0)
        finally:
            M.snapshot = saved_snap
            for k, val in saved_env.items():
                os.environ.pop(k, None) if val is None else os.environ.__setitem__(k, val)
        assert g["idle"] and rows[-1].get("admittedBy"), (g, rows)


# C1827 -- rejects the admitted rule dropping anything but the shell: probes, hosted probes, builds and the game count
def test_c1827_hold_admitted_caller_still_counts_probes_builds_and_the_game():
    for p in ({"name": "switchprobe.exe", "pid": 130, "cmd": "X:\\h\\switchprobe.exe --x"},
              {"name": "dotnet.exe", "pid": 131, "cmd": "dotnet exec X:\\h\\overlapprobe.dll --json"},
              {"name": "VBCSCompiler.exe", "pid": 132, "cmd": "VBCSCompiler.exe -pipename:x"},
              {"name": "SkyrimSE.exe", "pid": 133, "cmd": "SkyrimSE.exe"}):
        v = M.decide(snap(p), exists=no_lock, count_shells=False)
        assert not v["idle"], (p, v)


# C1828 -- rejects the shell passing anywhere a hold did not admit the caller (no slot, a foreign token, a dead
# holder, an unreadable slot file), including switchflip's own gate outside a hold
def test_c1828_without_an_admitting_hold_the_shell_still_blocks():
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        assert M.admitting_hold({}) is None
        assert M.admitting_hold(_slot_env(d, token="tok", file_token="other")) is None
        assert M.admitting_hold(_slot_env(d, pid=999999991)) is None
        assert M.admitting_hold({"IDLE_LOCK_SLOT": "tok", "IDLE_LOCK_SLOT_FILE": os.path.join(d, "missing")}) is None
        assert M.admitting_hold({"IDLE_LOCK_SLOT": "tok"}) is None
    import switchflip
    saved_env = {k: os.environ.pop(k, None) for k in ("IDLE_LOCK_SLOT", "IDLE_LOCK_SLOT_FILE")}
    saved_snap = M.snapshot
    M.snapshot = lambda: snap(SHELL_ROW)
    try:
        g = switchflip.idle_gate([], 16.0, lambda row: None, tries=1, wait_s=0)
    finally:
        M.snapshot = saved_snap
        for k, val in saved_env.items():
            if val is not None:
                os.environ[k] = val
    assert not g["idle"] and SHELL_WORDS in "; ".join(g["reasons"]), g


def main():
    tests =[(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    _runlog = os.environ.get("HOUSECARL_RUNLOG_DIR")   # the caller's run log (the harness-suites guard sets one)
    _code = main()
    if os.environ.get("HOUSECARL_RUNLOG_DIR") != _runlog:
        print(f"FAIL  the suite left HOUSECARL_RUNLOG_DIR as {os.environ.get('HOUSECARL_RUNLOG_DIR')!r}, not the caller's {_runlog!r}: "
              "a later program would write the user's shared run history")
        _code = 1
    sys.exit(_code)
