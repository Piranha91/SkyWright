"""orderpass_records - the records stage of a whole-order pass, over a pass folder orderpass.py has finished.

    python integration/housecarl/orderpass_records.py --pass DIR --instance DIR --profile NAME --game DIR
                                                      --reader EXE [--order FILE] [--deadline-min 150]

What this is for. orderpass.py (SkyWright's whole-order pass of its own probes) leaves its records stage unassessed,
because the record read goes through houseCARL's record reader (the record capture, evalrecords.capture_arm). This step
reads it: every plugin of the pass's map in one reader session, the lost-edit rows written to DIR/records.json, the
stage recorded in DIR/stages.json, then the leads built again (orderleads build), so they hold the record rows. A
refused read is recorded as refused, with its sentence, and the leads keep naming the check unassessed.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import skywright_paths as SW  # noqa: E402,F401  SkyWright's tools (orderleads) and run log on sys.path


def read(a, out: Path, m: dict) -> dict:
    """The record stage's row for stages.json, writing records.json on success."""
    t0 = time.time()
    if not a.reader or not (os.path.isfile(a.reader) or shutil.which(a.reader)):
        return {"seconds": round(time.time() - t0, 1), "exit": None, "stopped": None,
                "refused": f"no record reader: pass --reader (the houseCARL MCP server), not found: {a.reader}"}
    try:
        import evalrecords as ER
        plugins = [p["name"] for p in m["plugins"]]
        baseline = [p["name"] for p in m["plugins"] if p["baseGame"]]
        direct = {p["name"].lower(): {x.lower() for x in p["masters"]} for p in m["plugins"]}
        memo = {}

        def masters_of(n):
            n = n.lower()
            if n not in memo:
                seen, stack = set(), list(direct.get(n, ()))
                while stack:
                    x = stack.pop()
                    if x not in seen:
                        seen.add(x)
                        stack.extend(direct.get(x, ()))
                memo[n] = seen
            return memo[n]
        view = out / "records-view"
        view.mkdir(exist_ok=True)
        ini = Path(a.instance) / "profiles" / a.profile
        modlist = json.loads(Path(a.order).read_text(encoding="utf-8"))["Mods"] if a.order else m["folders"]
        ER.write_view(view, {"plugins": plugins, "mods": modlist}, ini)
        env = {"HouseCarl__DataDir": f"{a.game}/Data", "HouseCarl__ModsDir": f"{a.instance}/mods", "HouseCarl__ProfileDir": str(view),
               "HOUSECARL_DATA_DIR": str(out / "reader-data"), "HOUSECARL_RUNLOG_DIR": str(out / "reader-data" / "runs")}
        res = ER.capture_arm({"planId": "orderpass"}, {"armId": "pass", "plugins": plugins}, baseline, a.reader, env, out / "records-session",
                             ER.FieldScope(), lambda s: print("records:", s, flush=True), deadline_s=a.deadline_min * 60, masters_of=masters_of)
        (out / "records.json").write_text(json.dumps({k: {"rows": v[0], "coverage": v[1]} for k, v in res.items()}), encoding="utf-8")
        return {"seconds": round(time.time() - t0, 1), "exit": 0, "stopped": None}
    except Exception as ex:  # noqa: BLE001  recorded as the stage's refusal, as orderpass records any stage
        return {"seconds": round(time.time() - t0, 1), "exit": 1, "stopped": None, "refused": f"{type(ex).__name__}: {str(ex)[:400]}"}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="orderpass_records.py", description=__doc__.split("\n\n")[0])
    ap.add_argument("--pass", dest="pass_dir", required=True, help="the pass folder orderpass.py wrote")
    ap.add_argument("--instance", required=True, help="the MO2 instance folder")
    ap.add_argument("--profile", required=True, help="the MO2 profile name")
    ap.add_argument("--game", required=True, help="the game folder")
    ap.add_argument("--order", default=None, help="the candidate order file the pass read, if it read one")
    ap.add_argument("--reader", default=os.environ.get("HOUSECARL_READER"), help="the houseCARL MCP server")
    ap.add_argument("--deadline-min", type=float, default=150, help="the read's time limit")
    a = ap.parse_args(argv)
    out = Path(a.pass_dir)
    if not (out / "map.json").is_file() or not (out / "stages.json").is_file():
        print(f"REFUSED: {out} holds no map.json and stages.json: run orderpass.py on it first.", file=sys.stderr)
        return 2
    m = json.loads((out / "map.json").read_text(encoding="utf-8"))
    stages = json.loads((out / "stages.json").read_text(encoding="utf-8"))
    stages["records"] = read(a, out, m)
    (out / "stages.json").write_text(json.dumps(stages, indent=1), encoding="utf-8")
    print("records", json.dumps(stages["records"])[:200], flush=True)
    p = out / "pass.json"
    if p.is_file():
        doc = json.loads(p.read_text(encoding="utf-8"))
        doc["stages"] = stages
        p.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    import orderleads
    return orderleads.main(["build", "--pass", str(out)])


if __name__ == "__main__":
    sys.exit(main())
