"""Which viewprobe a plan launches, and what it says when it cannot use the built one.

    python test_shotplan_vp.py

No game, no renderer and no build: every case below fabricates a repository shape on disk and asks
the real resolver what it would run. One case uses the actual checkout, because that is the path
callers get.

Why this file exists. `vp_command` returned `dotnet run --project ... -c Release` whatever was on
disk, so every planner invocation paid a build check it did not need. Preferring a built executable
is worth that only if the preference is HONEST about the two cases where the built one is not the
right answer - it is not there at all, or it is older than the sources it was built from - because
the cost `dotnet run` was paying for is exactly that guarantee. So the contract checked here is a
pair: prefer the built executable, and when falling back, SAY SO with the reason.

The staleness question is asked of every project in THIS repository that the executable is built
from - the probe itself, the shared harness sources and the core library - because a change in any
of them makes the executable old. The rendering library lives in another repository and is not
checked; the fallback line says so rather than leaving a reader to assume it was.

The notice goes to stderr, where `Probe.run`'s own echo already goes, so a caller parsing stdout
sees nothing new (`capture_acquire.py` keeps shotplan's streams and its exit code).
"""
from __future__ import annotations

import contextlib
import io
import os
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shotplan as S

FAILS = []

OLD, NEW = time.time() - 4000, time.time() - 100     # mtimes are SET, never slept for


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


def call_vp_command(vp=None, root=None):
    """vp_command with its output captured. Returns (cmd, stdout, stderr)."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        cmd = S.vp_command(vp) if root is None else S.vp_command(vp, root=root)
    return cmd, out.getvalue(), err.getvalue()


def write(path, text, when):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    os.utime(path, (when, when))
    return path


def fabricate(base, *, exe=True, newer=None):
    """A repository shaped like this one: the probe, the shared sources, the core library, and
    optionally a built executable. `newer` names which source is NEWER than the executable."""
    root = os.path.join(base, "_geometry-harness", "viewprobe")
    files = {
        "viewprobe": os.path.join(root, "Program.cs"),
        "shared": os.path.join(base, "_geometry-harness", "_shared", "Worklist.cs"),
        "core": os.path.join(base, "src", "housecarl-core", "Progress", "RunLog.cs"),
    }
    for key, path in files.items():
        write(path, "// %s\n" % key, NEW if key == newer else OLD)
    # A stale build output must not be mistaken for a source: it sits under bin/ and is ignored.
    write(os.path.join(root, "bin", "Release", "net9.0-windows10.0.19041.0", "Generated.cs"),
          "// build output\n", NEW)
    exe_path = os.path.join(root, "bin", "Release", "net9.0-windows10.0.19041.0", "viewprobe.exe")
    if exe:
        os.makedirs(os.path.dirname(exe_path), exist_ok=True)
        with open(exe_path, "wb") as f:
            f.write(b"MZ")
        stamp = OLD + 1000                      # newer than an OLD source, older than a NEW one
        os.utime(exe_path, (stamp, stamp))
    return root, exe_path, files


# --------------------------------------------------------------------------- the cases --

def test_explicit_vp_is_unchanged():
    """The control. --vp has always won and must keep winning, whatever is on disk."""
    cmd, out, err = call_vp_command("C:/somewhere/viewprobe.exe --extra")
    check("explicit --vp is split and used verbatim",
          cmd == ["C:/somewhere/viewprobe.exe", "--extra"], repr(cmd))
    check("explicit --vp prints no notice", out == "" and err == "", repr(out + err))


def test_built_exe_is_preferred():
    with tempfile.TemporaryDirectory() as tmp:
        root, exe, _ = fabricate(tmp)
        cmd, out, err = call_vp_command(None, root=root)
        check("a built executable is what gets run", cmd == [exe], repr(cmd))
        check("...and no dotnet build check is paid for it",
              "dotnet" not in [os.path.basename(c).lower() for c in cmd], repr(cmd))
        check("...silently, because nothing was given up", out == "" and err == "", repr(out + err))


def test_no_executable_falls_back_and_says_so():
    with tempfile.TemporaryDirectory() as tmp:
        root, _, _ = fabricate(tmp, exe=False)
        cmd, out, err = call_vp_command(None, root=root)
        check("with no built executable the command is dotnet run",
              cmd[:2] == ["dotnet", "run"], repr(cmd))
        check("...against the root it was asked about", root in cmd, repr(cmd))
        check("...and the fallback is announced", err.strip() != "", repr(err))
        check("...naming the executable it looked for", "viewprobe.exe" in err, repr(err))
        check("...on stderr, never on the stdout a caller parses", out == "", repr(out))


def stale_case(which, filename):
    """An executable older than a source in ANY project this repository builds it from."""
    with tempfile.TemporaryDirectory() as tmp:
        root, _, files = fabricate(tmp, newer=which)
        cmd, out, err = call_vp_command(None, root=root)
        check("a %s source newer than the executable is not silently used" % which,
              cmd[:2] == ["dotnet", "run"], repr(cmd[:2]))
        check("...and the reason names %s" % filename, filename in err, repr(err))
        check("...on stderr", out == "", repr(out))


def test_stale_against_the_probe_itself():
    stale_case("viewprobe", "Program.cs")


def test_stale_against_the_shared_sources():
    stale_case("shared", "Worklist.cs")


def test_stale_against_the_core_library():
    stale_case("core", "RunLog.cs")


def test_the_fallback_says_what_it_could_not_check():
    """The rendering library is in another repository. A reader must not have to assume whether it
    was part of the staleness question."""
    with tempfile.TemporaryDirectory() as tmp:
        root, _, _ = fabricate(tmp, exe=False)
        _cmd, _out, err = call_vp_command(None, root=root)
        check("the fallback line names the repository it could not check",
              "repositor" in err.lower(), repr(err))


def test_a_build_output_is_not_a_source():
    """bin/ holds generated .cs files that are newer than the executable by construction. Counting
    them would make every run stale and the preference would never apply."""
    with tempfile.TemporaryDirectory() as tmp:
        root, exe, _ = fabricate(tmp)
        cmd, _out, err = call_vp_command(None, root=root)
        check("a generated file under bin/ does not make the executable stale",
              cmd == [exe], repr(cmd) + " " + repr(err))


def test_resolver_reports_both_halves():
    """The resolver is asked directly, because the drivers need the reason as data, not as a
    printed line."""
    fn = getattr(S, "viewprobe_exe", None)
    if fn is None:
        check("shotplan exposes a viewprobe_exe resolver", False,
              "no attribute `viewprobe_exe` in shotplan")
        return
    with tempfile.TemporaryDirectory() as tmp:
        root, exe, _ = fabricate(tmp)
        got = fn(root)
        check("resolver returns (path, reason)", isinstance(got, tuple) and len(got) == 2, repr(got))
        if isinstance(got, tuple) and len(got) == 2:
            check("...the path of the built executable", got[0] == exe, repr(got))
    with tempfile.TemporaryDirectory() as tmp:
        root, _, _ = fabricate(tmp, exe=False)
        got = fn(root)
        check("resolver returns no path when there is none",
              isinstance(got, tuple) and got[0] is None, repr(got))
        check("...with a reason a caller can print",
              isinstance(got, tuple) and isinstance(got[1], str) and len(got[1]) > 10, repr(got))


def test_this_checkout_default():
    """The path every caller actually takes. This checkout has a built viewprobe.exe, so the
    default command must be that executable - this is the case the packet is about."""
    built = os.path.join(S.VIEWPROBE_DIR, "bin", "Release", "net9.0-windows10.0.19041.0",
                         "viewprobe.exe")
    if not os.path.isfile(built):
        check("this checkout has a built viewprobe to prefer", False,
              "not built: %s - this case cannot decide anything here" % built)
        return
    cmd, out, err = call_vp_command(None)
    print("       vp_command(None) here -> %r" % (cmd,))
    if err.strip():
        print("       and it said: %s" % err.strip().splitlines()[0])
    check("the default command is not a dotnet build check",
          cmd[:2] != ["dotnet", "run"], repr(cmd))
    check("the default command is the built executable",
          cmd and os.path.normcase(cmd[0]) == os.path.normcase(built), repr(cmd[:1]))


def test_cell_is_still_the_first_argument_viewprobe_sees():
    """The Runner rule that predates this change and must survive it: the cell is args[0]
    LITERALLY, whatever the base command is."""
    with tempfile.TemporaryDirectory() as tmp:
        root, exe, _ = fabricate(tmp)
        try:
            r = S.Runner("SomeCell", vp=None, out=None, size=320, echo=False)
        except TypeError as e:
            check("a Runner can be built with no --vp", False, str(e)[:80])
            return
        base = r.base
        check("the base command ends where the cell begins",
              base and (base[-1] == "--" or os.path.basename(base[-1]).lower() == "viewprobe.exe"),
              repr(base))
        check("the Runner's cell is unchanged by the base command", r.cell == "SomeCell")
        cmd, _, _ = call_vp_command(None, root=root)
        check("nothing is appended after the executable that could take args[0]",
              cmd == [exe], repr(cmd))


def main():
    for fn in (test_explicit_vp_is_unchanged,
               test_built_exe_is_preferred,
               test_no_executable_falls_back_and_says_so,
               test_stale_against_the_probe_itself,
               test_stale_against_the_shared_sources,
               test_stale_against_the_core_library,
               test_the_fallback_says_what_it_could_not_check,
               test_a_build_output_is_not_a_source,
               test_resolver_reports_both_halves,
               test_this_checkout_default,
               test_cell_is_still_the_first_argument_viewprobe_sees):
        print(fn.__name__)
        try:
            fn()
        except TypeError as e:
            # `vp_command(vp, root=...)` not being reachable is itself the missing contract.
            check(fn.__name__ + ": reachable", False, "TypeError: " + str(e)[:90])
    print("\n%d checks failed" % len(FAILS))
    for f in FAILS:
        print("  ", f)
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
