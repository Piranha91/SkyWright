"""Known answers for ordersweep's program key (the cache's binary identity).

Dependency-free: `python _geometry-harness/tests/test_ordersweep_program_key.py`.
A probe's .exe is the apphost launcher, byte-identical across builds of the same project; the code that runs is in its
dlls. A deploy that replaces only the dlls must change the key, or --reuse-cache serves an older build's emit.
Each test names the wrong answer it rejects.
"""

import os
import shutil
import sys
import tempfile
import traceback
from pathlib import Path

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import ordersweep as O  # noqa: E402

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


def harness(root, files):
    """A harness folder with one probe's Release output: {relative path: bytes}."""
    exe = "fakeprobe.exe" if os.name == "nt" else "fakeprobe"
    out = Path(root) / "fakeprobe" / "bin" / "Release" / "net9.0"
    out.mkdir(parents=True)
    for rel, data in files.items():
        p = out / rel.replace("{exe}", exe)
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)
    return Path(root)


BASE = {"{exe}": b"apphost", "fakeprobe.dll": b"probe code v1", "housecarl-core.dll": b"core v1",
        "fakeprobe.runtimeconfig.json": b"{}", "runtimes/win/lib/net9.0/native.dll": b"native v1"}


def keys(a_files, b_files):
    with tempfile.TemporaryDirectory() as ta, tempfile.TemporaryDirectory() as tb:
        ka = O.program_key(harness(ta, a_files), "fakeprobe", {})
        kb = O.program_key(harness(tb, b_files), "fakeprobe", {})
    return ka, kb


def same_build_same_key():
    # rejects: a key that depends on where the build sits (two copies of one build must share cache entries)
    ka, kb = keys(BASE, dict(BASE))
    assert ka == kb, (ka, kb)


def probe_dll_changes_key():
    # rejects: the exe-only key; equal exes, a different probe dll (deploy4 replaced the dll, not the exe)
    ka, kb = keys(BASE, dict(BASE, **{"fakeprobe.dll": b"probe code v2"}))
    assert ka != kb, "equal exes and a different probe dll gave the same program key"


def core_dll_changes_key():
    # rejects: a key that covers the probe's own dll but not housecarl-core.dll
    ka, kb = keys(BASE, dict(BASE, **{"housecarl-core.dll": b"core v2"}))
    assert ka != kb, "equal exes and a different housecarl-core.dll gave the same program key"


def nested_dll_changes_key():
    # rejects: a key that reads only the top folder (runtime-specific dlls live under runtimes/)
    ka, kb = keys(BASE, dict(BASE, **{"runtimes/win/lib/net9.0/native.dll": b"native v2"}))
    assert ka != kb, "a different runtimes/ dll gave the same program key"


def added_dll_changes_key():
    # rejects: a key over a fixed list of names (a build that adds an assembly is a different program)
    ka, kb = keys(BASE, dict(BASE, **{"extra.dll": b"new dependency"}))
    assert ka != kb, "an added dll gave the same program key"


def symbols_do_not_change_key():
    # rejects: a key that misses on debug symbols alone (they do not run)
    ka, kb = keys(dict(BASE, **{"fakeprobe.pdb": b"symbols 1"}), dict(BASE, **{"fakeprobe.pdb": b"symbols 2"}))
    assert ka == kb, (ka, kb)


def same_folder_same_key_twice():
    # rejects: a key that is not a function of the folder's content (two calls on one unchanged folder must agree)
    with tempfile.TemporaryDirectory() as t:
        h = harness(t, BASE)
        assert O.program_key(h, "fakeprobe", {}) == O.program_key(h, "fakeprobe", {})


def run_time_subfolder_does_not_change_key():
    # rejects: a key that a probe's own run-time output changes (viewprobe writes shots/ into its bin folder)
    ka, kb = keys(BASE, dict(BASE, **{"shots/view-fp.png": b"a picture", "extract-123/x.nif": b"scratch"}))
    assert ka == kb, (ka, kb)


def copied_data_file_changes_key():
    # rejects: a key over code only (overlapprobe reads load-settle-exemptions.json from its own folder)
    ka, kb = keys(dict(BASE, **{"load-settle-exemptions.json": b"[1]"}), dict(BASE, **{"load-settle-exemptions.json": b"[2]"}))
    assert ka != kb, "a different data file copied to output gave the same program key"


def override_keeps_its_text_key():
    # rejects: a --probe-cmd fixture keyed by a Release folder it does not run
    k = O.program_key(Path("."), "fakeprobe", {"fakeprobe": "python fake.py"})
    assert k.startswith("probe-cmd:"), k


for name, fn in [("same build in two folders: same key", same_build_same_key),
                 ("equal exes, different probe dll: different keys", probe_dll_changes_key),
                 ("equal exes, different housecarl-core.dll: different keys", core_dll_changes_key),
                 ("different dll under runtimes/: different keys", nested_dll_changes_key),
                 ("an added dll: different keys", added_dll_changes_key),
                 ("symbols only differ: same key", symbols_do_not_change_key),
                 ("one unchanged folder keyed twice: same key", same_folder_same_key_twice),
                 ("files a run writes in a subfolder: same key", run_time_subfolder_does_not_change_key),
                 ("a different copied data file: different keys", copied_data_file_changes_key),
                 ("a --probe-cmd override keys by its text", override_keeps_its_text_key)]:
    check(name, fn)

print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
sys.exit(1 if FAILURES else 0)
