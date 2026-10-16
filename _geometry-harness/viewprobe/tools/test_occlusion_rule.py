#!/usr/bin/env python3
"""Runs viewprobe's occlusion-rule known answers (cases 6000-6014: the rule's orientation scope through the rule loader
and Shadow, on planes built in the test) against the built viewprobe. Fails, named, when viewprobe is not built.

    python -B _geometry-harness/viewprobe/tools/test_occlusion_rule.py
"""
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
BIN = HERE.parent / "bin" / "Release"


def main():
    exes = sorted(BIN.rglob("viewprobe.exe")) if BIN.is_dir() else []
    if not exes:
        print("FAIL: viewprobe is not built under %s; build the solution first" % BIN)
        return 1
    with tempfile.TemporaryDirectory(prefix="hc-occlusion-rule-") as tmp:
        r = subprocess.run([str(exes[0]), "--occlusion-rule-selftest", tmp], capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
    sys.stdout.write(r.stdout)
    sys.stderr.write(r.stderr)
    print("test_occlusion_rule:", "PASS" if r.returncode == 0 else "FAIL (exit %d)" % r.returncode)
    return 0 if r.returncode == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
