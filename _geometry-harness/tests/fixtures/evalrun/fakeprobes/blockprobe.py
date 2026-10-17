"""Fixture stand-in for blockprobe (route-cut): records its argv as JSON in EVALRUN_FAKE_ARGV_DIR and prints the two
lines the registry's text regexes read. No load order is read."""
import json
import os
import sys
import uuid
from pathlib import Path

if __name__ == "__main__":
    d = os.environ.get("EVALRUN_FAKE_ARGV_DIR")
    if d:
        Path(d).mkdir(parents=True, exist_ok=True)
        (Path(d) / f"{uuid.uuid4().hex}.json").write_text(json.dumps(sys.argv[1:]), encoding="utf-8")
    print("   ---- rule comparison, all on the same 7 references ----")
    print("   navmesh triangles that could not be judged (inset consumed them): 0")
    raise SystemExit(0)
