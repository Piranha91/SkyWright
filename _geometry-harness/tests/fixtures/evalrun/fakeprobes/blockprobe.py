"""Fixture stand-in for blockprobe (route-cut): records its argv as JSON in EVALRUN_FAKE_ARGV_DIR and prints the two
lines the registry's text regexes read. Given --rows FILE it writes a rows file of one LikelyBlocked row there, as
blockprobe --rows does (case 1443). No load order is read."""
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
    if "--rows" in sys.argv[1:]:
        out = Path(sys.argv[sys.argv.index("--rows") + 1])
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps({"Probe": "blockprobe", "Rows": [
            {"Verdict": "LikelyBlocked", "FormKey": "000801:FixtureTarget.esp", "Cell": "FixtureCell", "Passage": 96,
             "PassageCap": 160, "PassageShare": "about half the time", "Navcut": False}]}), encoding="utf-8")
    print("   ---- rule comparison, all on the same 7 references ----")
    print("   navmesh triangles that could not be judged (inset consumed them): 0")
    raise SystemExit(0)
