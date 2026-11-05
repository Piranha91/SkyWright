"""A stand-in for the machine's slot command in tests: takes a lock file exclusively (its pid inside), logs the take in
idle_lock.log beside it, runs the job as its own child process and releases the lock when the job ends. Like the real
holder, a hard kill of this process (the Runner's deadline stop) leaves the lock behind.

    python standin_hold.py --lock PATH --purpose TEXT --max-min N [any other slot option ...] -- CMD ...
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path


def log(lock, **rec):
    rec["t"] = time.strftime("%Y-%m-%d %H:%M:%S %Z")
    rec["lock"] = str(lock)
    with open(Path(lock).parent / "idle_lock.log", "a", encoding="utf-8") as fh:
        fh.write(json.dumps(rec) + "\n")


def main(argv):
    cut = argv.index("--")
    opts, cmd = argv[:cut], argv[cut + 1:]
    lock = Path(opts[opts.index("--lock") + 1])
    try:
        fd = os.open(str(lock), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        print(f"refusing: lock {lock} exists")
        return 3
    with os.fdopen(fd, "w", encoding="utf-8") as fh:
        json.dump({"pid": os.getpid(), "helper": "standin_hold.py"}, fh)
    log(lock, step="taken", pid=os.getpid())
    reason, code = "exception", 1
    try:
        code = subprocess.call(cmd)
        reason = "job-ended" if code == 0 else "job-failed"
        return code
    finally:
        os.remove(lock)
        log(lock, step="released", reason=reason, exitCode=code)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
