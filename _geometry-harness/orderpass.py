#!/usr/bin/env python
"""One whole-order pass: the order's map, every check over the whole order, the record stage, then the leads.

    orderpass.py --out DIR --instance DIR --profile NAME --game DIR [--order FILE] [--added PLUGIN|FOLDER ...]
                 [--skip STAGE ...] [--reader EXE] [--route-cap-targets N] [--deadline-min 150] [--sample-memory]

Stages, in order: map (ordermap), overlap, contact, empty (contactprobe --empty-space), census (switchprobe), state
(stateprobe over the census's generated flips), navown, seam, water, dep, drift (quest aliases), route (blockprobe, every
plugin with cells as a target) and records (the record reader's lost-edit rows). Each writes its file under DIR; a
stage that fails, is refused or is skipped is recorded in stages.json and its check is named unassessed in the leads.
pass.json records the order, what was added and the stages. Then orderleads writes index.json, LEADS.md (the added
mod's view when one was added, else the whole list) and LEADS-whole.md. Read-only on the instance.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
sys.path.insert(0, str(HERE))
from runlog import Runner  # noqa: E402

PROBES = ("ordermap", "overlapprobe", "contactprobe", "switchprobe", "stateprobe", "navownprobe", "seamprobe", "waterprobe", "depprobe", "driftprobe", "blockprobe")


def default_resolve(name):
    return [str(HERE / name / "bin" / "Release" / "net9.0" / f"{name}.exe")]


def _present(argv0):
    return os.path.isfile(argv0) or shutil.which(argv0) is not None


def _sampler(path, stop):
    ps = ("$p = Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'probe|ordermap|housecarl-mcp' -and $_.WorkingSetSize -gt 300000000 }; "
          "$o = Get-CimInstance Win32_OperatingSystem; "
          "foreach ($x in $p) { '{0},{1},{2},{3},{4}' -f $x.Name,$x.WorkingSetSize,$x.PrivatePageCount,$x.ReadTransferCount,$o.FreePhysicalMemory }")
    with open(path, "a", encoding="utf-8") as f:
        while not stop.is_set():
            r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True)
            for line in r.stdout.splitlines():
                f.write(f"{time.time():.0f},{line}\n")
            f.flush()
            stop.wait(10)


def main(argv=None, resolve=None):
    resolve = resolve or default_resolve
    ap = argparse.ArgumentParser(prog="orderpass.py")
    ap.add_argument("--out", required=True, help="the pass folder to write")
    ap.add_argument("--instance", required=True, help="the MO2 instance folder")
    ap.add_argument("--profile", required=True, help="the MO2 profile name")
    ap.add_argument("--game", required=True, help="the game folder")
    ap.add_argument("--order", default=None, help="a candidate order file instead of the profile's order")
    ap.add_argument("--added", action="append", default=[], help="an added mod's plugin or folder: its view is the pass's first read")
    ap.add_argument("--skip", action="append", default=[], help="a stage to leave out; it is named unassessed")
    ap.add_argument("--reader", default=os.environ.get("HOUSECARL_READER"), help="the houseCARL MCP server for the record stage")
    ap.add_argument("--route-cap-targets", type=int, default=0, help="0: every plugin with cells is a route target")
    ap.add_argument("--deadline-min", type=float, default=150, help="each stage's time limit")
    ap.add_argument("--sample-memory", action="store_true", help="sample probe memory every 10 s into memory.csv (Windows)")
    a = ap.parse_args(argv)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    common = ["--instance", a.instance, "--profile", a.profile, "--game", a.game] + (["--order", a.order] if a.order else [])
    stages = {}
    t_pass = time.time()

    def save():
        (out / "stages.json").write_text(json.dumps(stages, indent=1), encoding="utf-8")

    def record(name, t0, code, stopped=None, **more):
        stages[name] = {"seconds": round(time.time() - t0, 1), "exit": code, "stopped": stopped, **more}
        save()
        print(name, json.dumps(stages[name])[:200], flush=True)

    def run(name, program, args, env=None):
        if name in a.skip:
            stages[name] = {"skipped": True}
            save()
            return False
        t0 = time.time()
        argv_ = resolve(program) + args
        if not _present(argv_[0]):
            record(name, t0, None, refused=f"program not found: {argv_[0]} (build the solution, or pass the program's folder)")
            return False
        r = Runner("orderpass", out_dir=str(out), deadline=time.perf_counter() + a.deadline_min * 60).run([{"id": name, "argv": argv_, "env": env or {}}], echo=False)[0]
        record(name, t0, r.get("exitCode"), r.get("stopped"))
        return r.get("exitCode") == 0

    stop = threading.Event()
    if a.sample_memory:
        threading.Thread(target=_sampler, args=(str(out / "memory.csv"), stop), daemon=True).start()
    try:
        if not run("map", "ordermap", common + ["--out", str(out / "map.json")]) or not (out / "map.json").exists():
            print("orderpass: the map stage gave no map.json, so no check can be placed; stopping.", file=sys.stderr)
            return 2
        m = json.loads((out / "map.json").read_text(encoding="utf-8"))
        for name, program, extra in [("overlap", "overlapprobe", ["--emit", str(out / "overlap.json"), "--threads", "8"]),
                                     ("contact", "contactprobe", ["--emit", str(out / "contact.json"), "--threads", "8"]),
                                     ("empty", "contactprobe", ["--empty-space", str(out / "empty-rows.json"), "--emit", str(out / "empty.json"), "--threads", "8"]),
                                     ("census", "switchprobe", ["--json", str(out / "census.json")])]:
            run(name, program, common + extra)
        if "state" in a.skip:
            stages["state"] = {"skipped": True}
            save()
        elif (out / "census.json").exists():
            try:
                import stategen
                doc = stategen.generate_file(out / "census.json")
                (out / "states.txt").write_text(stategen.specs_text(doc), encoding="utf-8")
                run("state", "stateprobe", common + ["--switch-map", str(out / "census.json"), "--emit", str(out / "state.json"), "--empty-space",
                                                     str(out / "state-empty.json"), "--states-file", str(out / "states.txt"), "--threads", "8"])
            except Exception as ex:
                record("state", time.time(), None, refused=f"the census's flips could not be generated: {type(ex).__name__}: {ex}")
        else:
            record("state", time.time(), None, refused="no census.json from the census stage")
        for name, program, extra in [("navown", "navownprobe", ["--emit", str(out / "navown.json")]), ("seam", "seamprobe", ["--emit", str(out / "seam.json")]),
                                     ("water", "waterprobe", ["--emit", str(out / "water.json")]), ("dep", "depprobe", ["--json", str(out / "dep.json")]),
                                     ("drift", "driftprobe", ["--emit", str(out / "drift.json")])]:
            run(name, program, common + extra)
        targets = [p["name"] for p in m["plugins"] if not p["baseGame"] and (p["interior"] or p["exterior"]) and not p["error"]]
        cap = a.route_cap_targets or len(targets)
        (out / "route-targets.json").write_text(json.dumps({"reached": targets[:cap], "notReached": targets[cap:]}, indent=1), encoding="utf-8")
        route_env = {"HOUSECARL_MO2_INSTANCE": a.instance, "HOUSECARL_MO2_PROFILE": a.profile, "HOUSECARL_MO2_GAME": a.game, "HOUSECARL_GAME_PATH": a.game}
        run("route", "blockprobe", (["--order", a.order] if a.order else []) + targets[:cap] + ["--rows", str(out / "route.json")], env=route_env)
        records_stage(a, out, m, stages, record, save)
    finally:
        stop.set()
    order_id = {"instance": a.instance, "profile": a.profile, "order": a.order, "plugins": len(m["plugins"]),
                "pluginNames": [p["name"] for p in m["plugins"]]}
    (out / "pass.json").write_text(json.dumps({"tool": "orderpass", "schema": 1, "order": order_id, "added": a.added, "stages": stages,
                                               "seconds": round(time.time() - t_pass, 1)}, indent=1), encoding="utf-8")
    import orderleads
    return orderleads.main(["build", "--pass", str(out)])


def records_stage(a, out, m, stages, record, save):
    if "records" in a.skip:
        stages["records"] = {"skipped": True}
        save()
        return
    t0 = time.time()
    if not a.reader or not _present(a.reader):
        record("records", t0, None, refused=f"no record reader: pass --reader (the houseCARL MCP server), not found: {a.reader}")
        return
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
        record("records", t0, 0)
    except Exception as ex:
        record("records", t0, 1, refused=f"{type(ex).__name__}: {str(ex)[:400]}")


if __name__ == "__main__":
    sys.exit(main())
