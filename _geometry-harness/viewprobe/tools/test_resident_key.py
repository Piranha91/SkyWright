#!/usr/bin/env python3
"""Runs viewprobe's resident-key known answers (ResidentKey.cs: a real texture uploaded through SkyRender's
GlTextureManager under each texture policy is found under the key the retained scene looks it up by) against the built
viewprobe. Needs a GL context. Fails, named, when viewprobe is not built.

    python -B _geometry-harness/viewprobe/tools/test_resident_key.py
"""
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.append(str(HERE.parents[1]))
import buildlayout  # noqa: E402  where the build put viewprobe

EXE = buildlayout.program("viewprobe")


def main():
    if not EXE.is_file():
        print("FAIL: viewprobe is not built at %s; build the solution first (scripts/build.ps1)" % EXE)
        return 1
    r = subprocess.run([str(EXE), "--resident-key-selftest"], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    sys.stdout.write(r.stdout)
    sys.stderr.write(r.stderr)
    print("test_resident_key:", "PASS" if r.returncode == 0 else "FAIL (exit %d)" % r.returncode)
    return 0 if r.returncode == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
