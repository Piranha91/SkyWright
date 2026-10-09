"""Known answers for the idle gate (machineidle.py). Synthetic snapshots; `decide()` is pure.

Dependency-free: `python _geometry-harness/tests/test_machineidle.py`.
Each test names the wrong answer it rejects.
"""

import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
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
            os.environ["HOUSECARL_RUNLOG_DIR"] = tmp
            with contextlib.redirect_stdout(out):
                code = M.main(list(argv))
    finally:
        M.subprocess.run = real_run
        os.environ.pop("HOUSECARL_RUNLOG_DIR", None)
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
            os.environ["HOUSECARL_RUNLOG_DIR"] = tmp
            with contextlib.redirect_stdout(out):
                code = M.main([])
    finally:
        M.PS_SNAPSHOT = real_script
        os.environ.pop("HOUSECARL_RUNLOG_DIR", None)
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


def main():
    tests =[(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
