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


def main():
    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
