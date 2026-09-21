"""Run one viewprobe command SIMULTANEOUSLY n times and check the runs agree.

    renderagree.py --exe <path to viewprobe.exe> --work <dir> [-n 2] [--trials 1]
                   [--waive reports,masks] -- <every viewprobe argument except --out>

Why this exists. Several renders started at the same moment from one build folder used to share one
extraction folder, so they overwrote each other's unpacked assets while reading them - and because a
failed read on that path is swallowed, each still finished at exit 0 with a picture that was quietly
wrong. Exit codes cannot see that. OUTPUTS can: this driver renders the same command alone, then n
times at once, and compares the decoded pixels, the report, the console and the renderer's own
census of each simultaneous run against the solo one (`renderequiv.py`).

It is a regression test for any machine, not a one-off: point it at a build and a cell you have, and
a difference is a defect in the tool rather than in your load order. Exit 0 = every run agreed;
exit 1 = at least one did not (the differing parts are named); exit 2 = it could not run the test.

Each process is its own run record (this is a `runlog` runner with one child per render), so a
campaign driver that calls this inherits the timings and the attribution. The processes are started
TOGETHER on purpose, which is why the shared `Runner` - which runs its jobs one after another - is
not what launches them; the record shape it produces is reproduced here instead.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.abspath(os.path.join(HERE, "..", "..", "..", "scripts")))
import renderequiv as RE  # noqa: E402

try:
    from runlog import Run                     # the project's run records
except ImportError:                            # pragma: no cover - a checkout without scripts/
    Run = None


def foreign_renderer_running(exe):
    """Another viewprobe already running invalidates the test: its renders would be part of the
    contention this measures, and on a broken build they would share the folder under test."""
    try:
        out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True,
                             text=True).stdout.lower()
    except OSError:
        return False
    return os.path.basename(exe).lower() in out


def render(exe, args, out_dir, tag, parent=None):
    shutil.rmtree(out_dir, ignore_errors=True)
    os.makedirs(out_dir, exist_ok=True)
    log = out_dir + ".log"
    with open(log, "wb") as f:
        p = subprocess.Popen([exe, *args, "--out", out_dir], stdout=f, stderr=subprocess.STDOUT)
    return {"tag": tag, "proc": p, "out": out_dir, "log": log}


def finish(job):
    code = job["proc"].wait()
    with open(job["log"], encoding="utf-8", errors="replace") as f:
        console = f.read()
    return {"tag": job["tag"], "out": job["out"], "log": job["log"],
            "exit": code, "console": console}


def child_record(tag, result, parent_id, session):
    if Run is None:
        return None
    with Run(tag, mode="child", parent_run_id=parent_id, session_id=session,
             stage_source="external-opaque") as child:
        child.artifact(result["out"])
        if result["exit"] != 0:
            child.fail("exit %s" % result["exit"], exit_code=result["exit"])
        return child.run_id


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--exe", required=True)
    ap.add_argument("--work", required=True, help="a directory for the outputs; it is rewritten")
    ap.add_argument("-n", type=int, default=2, help="how many renders start together")
    ap.add_argument("--trials", type=int, default=1)
    ap.add_argument("--waive", default="", help="parts not to compare, by NAME (e.g. reports,masks)")
    ap.add_argument("--json", default="", help="write the whole result here")
    ap.add_argument("rest", nargs=argparse.REMAINDER,
                    help="-- then every viewprobe argument except --out")
    a = ap.parse_args(argv)
    args = [x for x in a.rest if x != "--"]
    if not args:
        ap.error("give the viewprobe arguments after --")
    if not os.path.isfile(a.exe):
        print("no such executable: %s" % a.exe)
        return 2
    if foreign_renderer_running(a.exe):
        print("refusing: a renderer is already running, so this test cannot mean anything")
        return 2
    waive = tuple(w for w in a.waive.split(",") if w)

    parent_id, session = None, os.environ.get("HOUSECARL_SESSION_ID")
    doc = {"exe": a.exe, "n": a.n, "trials": a.trials, "waived": list(waive), "trialResults": []}
    ctx = Run("renderagree", mode="runner", stage_source="counted", heartbeat=True,
              session_id=session) if Run is not None else None
    try:
        if ctx is not None:
            ctx.__enter__()
            parent_id = ctx.run_id
            ctx.feature("renders", a.n * a.trials + 1, "renders")
            ctx.stage("trials", "comparing simultaneous renders", a.trials, "trials")

        solo = finish(render(a.exe, args, os.path.join(a.work, "solo"), "renderagree-solo"))
        child_record("renderagree-solo", solo, parent_id, session)
        doc["solo"] = {"exit": solo["exit"], "census": RE.census(solo["console"])}
        if solo["exit"] != 0:
            print("the solo render failed (exit %s); nothing can be compared with it" % solo["exit"])
            doc["verdict"] = "the reference render failed"
            return 2

        bad_total = 0
        for t in range(1, a.trials + 1):
            jobs = [render(a.exe, args, os.path.join(a.work, "t%d-%d" % (t, i + 1)),
                           "renderagree-t%d-%d" % (t, i + 1)) for i in range(a.n)]
            started = time.perf_counter()
            group = [finish(j) for j in jobs]
            span = round(time.perf_counter() - started, 2)
            rows = []
            for r in group:
                child_record(r["tag"], r, parent_id, session)
                c = RE.compare(solo["out"], r["out"], solo["console"], r["console"],
                               solo["exit"], r["exit"], waive=waive)
                rows.append({"render": r["tag"], "exit": r["exit"], "equal": c["equal"],
                             "parts": {k: v["status"] for k, v in c["parts"].items()},
                             "notEqual": [k for k, v in c["parts"].items()
                                          if v["status"] == RE.DIFFERENT]})
            bad = [r for r in rows if not r["equal"]]
            bad_total += len(bad)
            doc["trialResults"].append({"trial": t, "seconds": span, "renders": rows,
                                        "differing": len(bad)})
            print("trial %d: %d of %d renders differ from the solo run%s"
                  % (t, len(bad), a.n,
                     "" if not bad else " — " + "; ".join("%s: %s" % (r["render"], ",".join(r["notEqual"]))
                                                          for r in bad)))
            if ctx is not None:
                ctx.advance()
                ctx.tally(ok=a.n - len(bad), failed=len(bad))
        doc["differingRenders"] = bad_total
        doc["verdict"] = ("every simultaneous render matched the solo render" if bad_total == 0
                          else "%d simultaneous render(s) did not match the solo render" % bad_total)
        print(doc["verdict"])
        return 0 if bad_total == 0 else 1
    finally:
        if a.json:
            with open(a.json, "w", encoding="utf-8") as f:
                json.dump(doc, f, indent=1)
        if ctx is not None:
            ctx.__exit__(None, None, None)


if __name__ == "__main__":
    sys.exit(main())
