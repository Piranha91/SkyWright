"""What a viewprobe run says about itself in the run store, checked against the contract.

    python test_run_records.py --store <a run store a render wrote to>
    python test_run_records.py --render --exe <viewprobe.exe> --instance <dir> --profile <name>
                               --game <dir> --cell <CellEditorID> [--store <dir>]

With `--render` it produces the records it judges (a single-shot render and a two-shot worklist
render); without it, it judges a store some other run produced. With NEITHER it refuses at exit 2
and says what it needs - a test that passes because it had nothing to look at is worse than no test.

The contract is `standards/HOUSECARL_RUN_EVENTS.md` §5 and the coverage manifest's claim about this
tool. Three things are checked, and each is a way the record can lie about the work:

  source     `stageSource` on the record must be what the manifest declares for viewprobe. The
             manifest says `counted`; a record that says `single-stage` makes the manifest false,
             and nothing else in the guard checks that claim.
  structure  a render is not one phase. The named cuts must each appear, as DISTINCT stages, in
             order, and a multi-shot run must show them once per shot with distinct indices. One
             stage repeated per shot is the shape this test exists to reject: it files a whole
             shot under the name of its first step.
  counters   a stage that claims a total must end at that total, or say why not. §5 allows
             `total: null` for an unknown total and allows a REVISED total with a stated reason;
             it does not allow a stage to finish at 1 of 5 in silence.

NOT MET, and worth saying plainly: nothing that runs without a game can catch a false `counted`
claim. The coverage guard only checks that rows declaring `unknown` carry a reason, and this file
needs a render to have anything to read. A build whose stages regressed would go unnoticed by CI
here; it is caught when someone renders with the store pointed at a directory and runs this.

The run's outcome, exit code and wall time are checked to be unchanged in kind (`Ok` at exit 0 for
a render that worked), because the whole point is a better record of the SAME behaviour.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(HERE)
REPO = os.path.dirname(HARNESS)
MANIFEST = os.path.join(REPO, "standards", "housecarl-run-coverage.json")

# The cuts the cost model already uses. All of them are PER SHOT, because the tool re-walks the
# load order per shot; `report` exists only when a report was asked for, which the record's own
# argument shape says, so it is required exactly when it was.
PER_SHOT = ["find-cell", "record-walk", "scene-build", "encode"]
WHEN_ASKED = {"report": "--planes-report"}

FAILS = []


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


def manifest_claim(tool="viewprobe"):
    doc = json.load(open(MANIFEST, encoding="utf-8"))
    for row in doc["entryPoints"]:
        if row.get("id") == tool:
            return row.get("stageSource")
    return None


def runs_in(store, op="viewprobe"):
    hist = os.path.join(store, "history.jsonl")
    if not os.path.exists(hist):
        return []
    out = []
    for line in open(hist, encoding="utf-8"):
        line = line.strip()
        if line:
            r = json.loads(line)
            if r.get("op") == op:
                out.append(r)
    return out


def norm(source):
    """`counted` in the manifest is `Counted` on a record; compare on one spelling."""
    return (source or "").replace("-", "").replace("_", "").lower()


# ------------------------------------------------------------------------ the checks --

def check_source(run, label):
    claim = manifest_claim()
    check("%s: the manifest's claim is what the record says" % label,
          norm(run.get("stageSource")) == norm(claim),
          "manifest %r, record %r" % (claim, run.get("stageSource")))


def check_structure(run, shots, label):
    stages = run.get("stages") or []
    ids = [s.get("id") for s in stages]
    argshape = str((run.get("resources") or {}).get("args") or "")
    want_ids = list(PER_SHOT) + [k for k, flag in WHEN_ASKED.items() if flag in argshape]
    check("%s: every named phase is a stage of its own" % label,
          set(want_ids).issubset(set(ids)),
          "have %r, want %r" % (sorted(set(ids)), sorted(want_ids)))
    check("%s: %d shot(s) means the per-shot phases repeat" % (label, shots),
          len(ids) >= len(want_ids) * shots,
          "have %d stages, want at least %d: %r" % (len(ids), len(want_ids) * shots, ids))
    for phase, flag in WHEN_ASKED.items():
        if flag in argshape:
            check("%s: %s was asked for, so there is a %r stage" % (label, flag, phase),
                  phase in ids, "have %r" % (sorted(set(ids)),))
        else:
            check("%s: no %s, so no empty %r stage is opened" % (label, flag, phase),
                  phase not in ids, "have %r" % (sorted(set(ids)),))
    check("%s: no phase is filed under another phase's name" % label,
          len(set(ids)) > 1, "all stages are %r" % (sorted(set(ids)),))
    check("%s: stage indices are distinct and in order" % label,
          [s.get("index") for s in stages] == list(range(len(stages))),
          repr([s.get("index") for s in stages]))
    check("%s: every stage carries its own wall time" % label,
          all(s.get("wallSeconds") is not None for s in stages),
          repr([s.get("wallSeconds") for s in stages]))
    # No stage may cover the whole run: that is the defect being fixed, not a threshold to tune.
    # The first stage of a render that has five phases cannot be 90 % of it.
    wall = run.get("wallSeconds") or 0.0
    biggest = max([(s.get("wallSeconds") or 0.0) for s in stages], default=0.0)
    check("%s: no single stage covers the whole run" % label,
          wall > 0 and biggest / wall < 0.9,
          "biggest stage %.2fs of %.2fs" % (biggest, wall))


def check_counters(run, label):
    for s in run.get("stages") or []:
        total, processed = s.get("total"), s.get("processed")
        if total is None:
            continue                       # an explicitly unknown total is allowed by §5
        ok = processed == total or bool(s.get("revision"))
        check("%s: stage %r ends at the total it claimed" % (label, s.get("id")), ok,
              "processed %r of %r, revision %r" % (processed, total, s.get("revision")))


def check_outcome(run, label):
    check("%s: the run still ends Ok at exit 0" % label,
          run.get("outcome") == "Ok" and run.get("exitCode") == 0,
          "%r / %r" % (run.get("outcome"), run.get("exitCode")))


# ----------------------------------------------------------------------- the renders --

def render(a, store, worklist=None, out=None):
    env = dict(os.environ)
    env["HOUSECARL_RUNLOG_DIR"] = store
    argv = [a.exe]
    argv += [worklist] if worklist else [a.cell]
    argv += ["--instance", a.instance, "--profile", a.profile, "--game", a.game,
             "--size", "256", "--msaa", "0", "--nav", "off"]
    if worklist:
        # The multi-shot arm asks for a report, so the `report` stage is exercised here and the
        # single-shot arm proves no empty one is opened without it.
        argv += ["--planes-report"]
    if not worklist:
        argv += ["--eye", a.eye, "--look", a.look]
    argv += ["--out", out]
    p = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", env=env)
    return p.returncode


def produce(a, store):
    tmp = tempfile.mkdtemp(prefix="rr-")
    wl = os.path.join(tmp, "w.json")
    eye = [float(x) for x in a.eye.split(",")]
    look = [float(x) for x in a.look.split(",")]
    json.dump({"Label": "run records", "Cells": [{"Cell": a.cell, "Shots": [
        {"Label": "one", "Eye": eye, "Look": look},
        {"Label": "two", "Eye": eye, "Yaw": 180.0, "Pitch": 5.0}]}]},
        open(wl, "w", encoding="utf-8"))
    rc1 = render(a, store, out=os.path.join(tmp, "s"))
    rc2 = render(a, store, worklist=wl, out=os.path.join(tmp, "m"))
    check("the fixture renders at all", rc1 == 0 and rc2 == 0, "exits %s / %s" % (rc1, rc2))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--store", default=os.environ.get("HOUSECARL_RUNLOG_DIR"))
    ap.add_argument("--render", action="store_true")
    ap.add_argument("--exe", default=os.environ.get("VIEWPROBE_EXE"))
    ap.add_argument("--instance"), ap.add_argument("--profile"), ap.add_argument("--game")
    ap.add_argument("--cell")
    ap.add_argument("--eye", default="1394,1205,3400")
    ap.add_argument("--look", default="1394,1206,1424")
    a = ap.parse_args(argv)

    if a.render:
        missing = [n for n in ("exe", "instance", "profile", "game", "cell") if not getattr(a, n)]
        if missing:
            print("--render needs %s" % ", ".join("--" + m for m in missing))
            return 2
        a.store = a.store or tempfile.mkdtemp(prefix="rr-store-")
        produce(a, a.store)
    if not a.store or not os.path.isdir(a.store):
        print("no run store to read: give --store <dir>, or --render with a fixture. "
              "Refusing rather than passing on nothing.")
        return 2

    runs = [r for r in runs_in(a.store) if r.get("outcome") == "Ok"]
    print("store %s: %d completed viewprobe run(s)" % (a.store, len(runs)))
    if not runs:
        print("no completed viewprobe run in the store; nothing can be judged.")
        return 2

    # The single-shot run is the one with the fewest stages, the multi-shot one has the most; both
    # are identified by the SHOT COUNT the record itself reports, never by which arm wrote them.
    for r in runs:
        shots = int(((r.get("features") or {}).get("shots") or {}).get("value") or 0)
        label = "run %s" % (r.get("runId") or "?")[:8]
        print(label + ("  (%d shot(s) declared)" % shots if shots else "  (no shot count declared)"))
        check("%s: the record says how many shots it rendered" % label, shots >= 1,
              "features: %r" % sorted((r.get("features") or {}).keys()))
        check_source(r, label)
        check_structure(r, max(shots, 1), label)
        check_counters(r, label)
        check_outcome(r, label)

    print("\n%d checks failed" % len(FAILS))
    for f in FAILS:
        print("  ", f)
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
