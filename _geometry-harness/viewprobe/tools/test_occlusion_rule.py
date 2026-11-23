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
sys.path.append(str(HERE.parents[1]))
import buildlayout  # noqa: E402  where the build put viewprobe

EXE = buildlayout.program("viewprobe")


def main():
    if not EXE.is_file():
        print("FAIL: viewprobe is not built at %s; build the solution first (scripts/build.ps1)" % EXE)
        return 1
    with tempfile.TemporaryDirectory(prefix="hc-occlusion-rule-") as tmp:
        r = subprocess.run([str(EXE), "--occlusion-rule-selftest", tmp], capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
    sys.stdout.write(r.stdout)
    sys.stderr.write(r.stderr)
    print("test_occlusion_rule:", "PASS" if r.returncode == 0 else "FAIL (exit %d)" % r.returncode)
    return 0 if r.returncode == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
