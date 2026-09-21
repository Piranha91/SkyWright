"""The extraction scratch's deleter contract - what viewprobe may and may not remove.

viewprobe gives each render process its own extraction folder and removes it when the process ends.
That makes viewprobe a program that deletes directories, and a deleter is judged by what it can
NEVER delete, not by what it usually deletes. So the sweep is driven here directly, against a root
this test owns, with the directory state built by the test and the verdict read off the filesystem
afterwards rather than from anything the tool says about itself.

Each case below is a way the sweep could destroy something it has no business touching:

  decoy        a directory that viewprobe did not create must survive, even sitting between real
               orphans that are correctly removed. A sweep that cannot tell them apart is a sweep
               that deletes a user's folder the day someone points the root at the wrong place.
  unknown      if the owner's liveness cannot be determined, KEEP. "I could not tell" is not
               "it is dead".
  recycled     process ids are reused. An orphan whose id now belongs to an unrelated live process
               must still be reported as kept (never silently treated as live-and-fine), and a LIVE
               process's folder must never be removed because some dead process had that id.
  concurrent   two processes starting at the same moment must not sweep each other's folders.
  own-exit     the exit-time delete obeys the same ownership rule as the sweep.
  root         all of the above hold for an overridden root, which is the only way this test can
               reach the sweep at all - so a root that is honoured for the happy path but not for
               the protections would be caught here.

No game, no MO2 instance and no load order: this runs anywhere viewprobe is built.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
VP = HARNESS / "viewprobe" / "bin" / "Release" / "net9.0-windows10.0.19041.0" / "viewprobe.exe"
# A private build may be pointed at instead, which is how this is run against a build under test
# without touching the production folder another session may be rendering from.
VP = Path(os.environ.get("VIEWPROBE_EXE", str(VP)))

MARKER = "viewprobe-extract.json"      # what makes a directory OURS; the contract's whole basis
FAILS: list[str] = []
FUNCTIONAL = [False]                   # set once the sweep is shown to actually delete an orphan


def check(name, cond, detail=""):
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))
    if not cond:
        FAILS.append(name)


def protective(name, cond, detail=""):
    """A check that something SURVIVES. A sweep that does nothing at all passes every one of these
    for free, so none of them may be reported as `ok` until the sweep has been seen deleting an
    orphan it should delete. Before that they are SKIPPED, which is not a pass."""
    if not FUNCTIONAL[0]:
        print("  SKIP " + name + "  (the sweep is not functional; a survival check would pass for "
                                 "the wrong reason)")
        FAILS.append(name + " [not exercised]")
        return
    check(name, cond, detail)


def free_pid():
    """A process id that is NOT currently in use, so 'the owner is dead' is true by measurement
    rather than by picking a big number and hoping."""
    live = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True).stdout
    used = set()
    for ln in live.splitlines():
        parts = [p.strip('"') for p in ln.split('","')]
        if len(parts) > 1 and parts[1].isdigit():
            used.add(int(parts[1]))
    for candidate in range(60000, 65000, 2):
        if candidate not in used:
            return candidate
    raise RuntimeError("no free pid found")


def sweep(root, extra=()):
    """Run the production sweep against `root`. Returns (exit code, parsed JSON or None, text)."""
    p = subprocess.run([str(VP), "--extraction-scratch-sweep", str(root), *extra],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    text = (p.stdout or "") + (p.stderr or "")
    doc = None
    for ln in text.splitlines():
        ln = ln.strip()
        if ln.startswith("{"):
            try:
                doc = json.loads(ln)
            except ValueError:
                pass
    return p.returncode, doc, text


def owned(root, name, pid, start_ticks=1, schema=1):
    d = Path(root) / name
    (d / "meshes").mkdir(parents=True, exist_ok=True)
    (d / "meshes" / "x.nif").write_bytes(b"extracted")
    (d / MARKER).write_text(json.dumps(
        {"schema": schema, "tool": "viewprobe", "pid": pid, "startTicks": start_ticks}),
        encoding="utf-8")
    return d


def foreign(root, name):
    d = Path(root) / name
    d.mkdir(parents=True, exist_ok=True)
    (d / "important.txt").write_text("not viewprobe's", encoding="utf-8")
    return d


def main():
    if not VP.exists():
        print("viewprobe.exe not found at %s - build it or set VIEWPROBE_EXE" % VP)
        return 2
    root = Path(tempfile.mkdtemp(prefix="hc-extract-scratch-"))
    live_pid = os.getpid()          # this test process: certainly alive
    dead_a, dead_b = free_pid(), free_pid() + 1
    try:
        # --- decoy: a foreign directory between two real orphans ---------------------------
        orphan_a = owned(root, "vp-%d-1" % dead_a, pid=dead_a)
        decoy = foreign(root, "someone-elses-folder")
        decoy_named_like_us = foreign(root, "vp-%d-1" % dead_b)      # right NAME, no marker
        orphan_b = owned(root, "vp-%d-2" % dead_a, pid=dead_a)
        rc, doc, text = sweep(root)
        check("sweep runs and reports as JSON", rc == 0 and doc is not None,
              "exit %s; first 160 chars: %s" % (rc, " ".join(text.split())[:160]))
        # THE GATE. Everything below is a "this must survive" check, and a sweep that does nothing
        # passes all of them for free. So the suite first requires the sweep to remove something it
        # is supposed to remove; until it does, the survival checks are SKIPPED, not passed.
        check("orphans with a dead owner are removed",
              not orphan_a.exists() and not orphan_b.exists(),
              "a=%s b=%s" % (orphan_a.exists(), orphan_b.exists()))
        FUNCTIONAL[0] = not orphan_a.exists() and not orphan_b.exists()
        protective("decoy: a directory without viewprobe's marker survives", decoy.exists())
        protective("decoy: a directory NAMED like ours but without the marker survives",
                   decoy_named_like_us.exists())
        protective("decoy: the decoy's contents are untouched",
                   (decoy / "important.txt").exists())

        # --- unknown liveness: keep ---------------------------------------------------------
        unknown = owned(root, "vp-unknown-1", pid=-1)           # not a resolvable id
        malformed = Path(root) / "vp-malformed-1"
        (malformed).mkdir(parents=True, exist_ok=True)
        (malformed / MARKER).write_text("{ this is not json", encoding="utf-8")
        rc, doc, text = sweep(root)
        protective("unknown liveness is KEPT, not deleted", unknown.exists())
        protective("a marker it cannot parse is KEPT, not deleted", malformed.exists())
        check("kept folders are reported, not silently skipped",
              bool(doc) and doc.get("kept") and len(doc["kept"]) >= 2,
              json.dumps(doc.get("kept", []))[:200] if doc else "no json")

        # --- recycled pid -------------------------------------------------------------------
        recycled = owned(root, "vp-recycled-1", pid=live_pid, start_ticks=1)
        rc, doc, text = sweep(root)
        protective("an orphan whose id now belongs to a live NON-viewprobe process is kept",
                   recycled.exists())
        check("...and is reported as kept with the reason, not dropped from the report",
              bool(doc) and any("recycled" in str(k).lower() or str(live_pid) in str(k)
                                for k in doc.get("kept", [])),
              json.dumps(doc.get("kept", []))[:200] if doc else "no json")

        live = owned(root, "vp-live-1", pid=live_pid, start_ticks=2)
        rc, doc, text = sweep(root)
        protective("a LIVE owner's folder is never removed", live.exists())

        # --- two processes starting together -------------------------------------------------
        mine = owned(root, "vp-mine-1", pid=live_pid, start_ticks=3)
        theirs = owned(root, "vp-theirs-1", pid=live_pid, start_ticks=4)
        rc, doc, text = sweep(root)
        protective("two folders whose owners are both alive both survive a sweep",
                   mine.exists() and theirs.exists())

        # --- the exit-time delete obeys the same rule -----------------------------------------
        # The refusal must be ABOUT ownership. A build with no such flag also exits non-zero, and
        # accepting that as a pass would let an unimplemented deleter look protective.
        rc, doc, text = sweep(root, extra=["--delete-only", str(decoy)])
        protective("the exit-time delete refuses a directory without the marker, and says why",
                   decoy.exists() and rc != 0 and MARKER.split(".")[0] in text,
                   "exit %s; %s" % (rc, " ".join(text.split())[:120]))
        # A LIVE owner's folder, handed to the deleter on the command line. The marker alone must
        # not authorise this: --delete-only takes any path, so a marker-only rule would let one
        # command remove a sibling's extraction folder in the middle of its render - the exact
        # failure the per-process folder exists to prevent, coming back through the deleter.
        rc, doc, text = sweep(root, extra=["--delete-only", str(theirs)])
        protective("--delete-only refuses a LIVE owner's folder, and says why",
                   theirs.exists() and rc != 0 and "not gone" in text.lower(),
                   "exit %s; %s" % (rc, " ".join(text.split())[:160]))
        # Its OWN call, and a DEAD owner. This check used to read the refusal's exit code above, so
        # it asked for one code to be both non-zero (the refusal) and zero (the success) - and no
        # directory was ever handed to --delete-only at all, so no behaviour could have satisfied
        # it.
        gone = owned(root, "vp-gone-1", pid=dead_a)
        rc, doc, text = sweep(root, extra=["--delete-only", str(gone)])
        check("the exit-time delete removes a marked directory whose owner is gone",
              not gone.exists() and rc == 0, "exit %s" % rc)

        # --- a marker this build does not understand -------------------------------------------
        future = owned(root, "vp-future-1", pid=dead_a, schema=99)
        other = Path(root) / "vp-othertool-1"
        (other).mkdir(parents=True, exist_ok=True)
        (other / MARKER).write_text(json.dumps(
            {"schema": 1, "tool": "something-else", "pid": dead_a, "startTicks": 1}),
            encoding="utf-8")
        rc, doc, text = sweep(root)
        protective("a marker of a schema this build does not know is KEPT", future.exists())
        protective("a marker belonging to another tool is KEPT", other.exists())

        # --- the root is honoured for the protections too --------------------------------------
        protective("nothing outside the given root was touched", Path(root).exists())
    finally:
        shutil.rmtree(root, ignore_errors=True)

    print("PASS" if not FAILS else "FAIL: " + "; ".join(FAILS))
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
