"""Does a frame name a command that reproduces THAT frame?

    python test_replay_command.py --exe <viewprobe.exe> --instance <dir> --profile <name>
                                  --game <dir> --cell <CellEditorID> [--work <dir>]

This one renders: it writes a two-shot worklist, renders it, and then asks each frame to reproduce
itself from the string it carries in its own tEXt chunk. Without a fixture to render it refuses at
exit 2 rather than passing on nothing.

What is being protected. Every picture carries `args: …`, and a replay from those arguments is
meant to be byte-exact. The string is built ONCE per process from the command line, so in a
multi-shot run every frame carries the same one - the worklist's command, not the shot's. Two
consequences, both checked below:

  the side file   the string keeps the worklist's PATH. Once that file is gone - which is what a
                  campaign driver does with the one-shot worklist it wrote per render - the string
                  reproduces nothing at all. So the frames are replayed here with the worklist
                  MOVED AWAY: the question is whether the frame alone is enough, and a side file
                  the frame does not carry is not part of the frame.
  the whole run   with the file still present the string re-renders the WHOLE worklist. Replaying
                  one frame of a twelve-shot batch by rendering twelve is not "a command that
                  reproduces that frame", and in a batch it is eleven renders of waste.

The green control comes first: the two shots must differ in decoded pixels, or every comparison
below would pass for the wrong reason.

The second arm is the shot a command line cannot express. A worklist shot may carry synthetic
props or several coloured routes, including measured ones, and no flag says those. A camera-only
command for such a shot would render a DIFFERENT picture and claim to be that frame, which is
worse than needing a side file - so that shot keeps the worklist-based string and has to say, in
the same breath, that the worklist is required and what in the shot requires it.

Both arms also check the reports beside the frames: the per-shot configuration sidecar and the
planes report must name the same command their frame does, since a report is as much a thing found
on disk later as a picture is.
"""
from __future__ import annotations

import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "viewprobe", "tools"))
import renderequiv as RE  # noqa: E402

FAILS = []


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


def png_meta(png):
    from PIL import Image
    with Image.open(png) as im:
        raw = (dict(im.text) if hasattr(im, "text") else {}).get("viewprobe", "")
    args = raw.split("args:", 1)[1].split("| pose:", 1)[0].strip() if "args:" in raw else ""
    return args, raw


def pngs(d):
    return sorted(os.path.join(d, f) for f in os.listdir(d) if f.lower().endswith(".png"))


def run(argv, out, store=None):
    env = dict(os.environ)
    if store:
        env["HOUSECARL_RUNLOG_DIR"] = store
    p = subprocess.run([*argv, "--out", out], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=env)
    return p.returncode, ((p.stdout or "") + (p.stderr or ""))


def check_unexpressible(a, work, paths):
    """A shot carrying a measured route - points the command line has no form for. Its frame must
    NOT name a standalone command: a picture without the route is not that frame."""
    wl = os.path.join(work, "u.json")
    eye = [float(x) for x in a.eye.split(",")]
    look = [float(x) for x in a.look.split(",")]
    json.dump({"Label": "unexpressible", "Cells": [{"Cell": a.cell, "Shots": [
        {"Label": "plain", "Eye": eye, "Look": look, "CullAbove": 1600.0},
        {"Label": "with a measured path", "Eye": eye, "Look": look, "CullAbove": 1600.0,
         "Routes": [{"Points": [[eye[0], eye[1] - 200.0, 1424.0],
                                [eye[0] + 200.0, eye[1] - 200.0, 1424.0]],
                     "Color": "cyan", "Label": "measured"}]}]}]},
        open(wl, "w", encoding="utf-8"), indent=1)
    out = os.path.join(work, "unexpressible")
    shutil.rmtree(out, ignore_errors=True)
    rc, text = run([a.exe, wl, *paths, "--size", a.size, "--msaa", "0", "--nav", "off"], out)
    check("the run with an inexpressible shot renders", rc == 0,
          "exit %s: %s" % (rc, text.strip()[-140:]))
    got = pngs(out) if os.path.isdir(out) else []
    if len(got) != 2:
        check("it produced both frames", False, repr([os.path.basename(g) for g in got]))
        return
    plain, routed = png_meta(got[0])[0], png_meta(got[1])[0]
    check("the plain shot still names a standalone command",
          "--eye" in plain and "needs the worklist" not in plain, plain[:110])
    check("the shot with a measured path does not name a standalone command",
          "needs the worklist" in routed, routed[:140])
    check("...and says what in the shot requires the file", "routes" in routed, routed[:140])
    check("...keeping the worklist path, so the frame is still reproducible with it",
          os.path.basename(wl) in routed, routed[:140])


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--exe", default=os.environ.get("VIEWPROBE_EXE"))
    ap.add_argument("--instance"), ap.add_argument("--profile"), ap.add_argument("--game")
    ap.add_argument("--cell")
    ap.add_argument("--eye", default="1394,1205,3400")
    ap.add_argument("--look", default="1394,1206,1424")
    ap.add_argument("--eye2", default="1394,1205,1700")
    ap.add_argument("--work", default=None)
    ap.add_argument("--size", default="512")
    a = ap.parse_args(argv)
    missing = [n for n in ("exe", "instance", "profile", "game", "cell") if not getattr(a, n)]
    if missing:
        print("needs %s - refusing rather than passing on nothing"
              % ", ".join("--" + m for m in missing))
        return 2

    work = a.work or tempfile.mkdtemp(prefix="rp-")
    os.makedirs(work, exist_ok=True)
    wl = os.path.join(work, "w.json")
    json.dump({"Label": "replay", "Cells": [{"Cell": a.cell, "Shots": [
        {"Label": "one", "Eye": [float(x) for x in a.eye.split(",")],
         "Look": [float(x) for x in a.look.split(",")], "CullAbove": 1600.0},
        {"Label": "two", "Eye": [float(x) for x in a.eye2.split(",")], "Yaw": 180.0,
         "Pitch": 5.0, "CullAbove": 1600.0}]}]},
        open(wl, "w", encoding="utf-8"), indent=1)

    paths = ["--instance", a.instance, "--profile", a.profile, "--game", a.game]
    base = os.path.join(work, "base")
    shutil.rmtree(base, ignore_errors=True)
    rc, text = run([a.exe, wl, *paths, "--size", a.size, "--msaa", "0", "--nav", "off",
                    "--config-identity", "--planes-report"], base)
    check("the two-shot run renders", rc == 0, "exit %s: %s" % (rc, text.strip()[-160:]))
    frames = pngs(base)
    check("it produced two frames", len(frames) == 2, repr([os.path.basename(f) for f in frames]))
    if len(frames) != 2:
        print("\n%d checks failed" % len(FAILS))
        return 1

    # ---- the green control: the comparator can tell these two apart ----------------
    d = RE.pixels_differ(frames[0], frames[1])
    check("control: the two shots differ in decoded pixels", bool(d.get("different")),
          "%s pixels" % d.get("different"))

    metas = [png_meta(f) for f in frames]
    check("each frame names a command of its own",
          metas[0][0] != metas[1][0],
          "both say: %s" % metas[0][0][:120])

    # ---- each frame, replayed from what IT says, with the side file gone ----------
    moved = wl + ".moved"
    os.replace(wl, moved)
    try:
        for i, (frame, (args, _raw)) in enumerate(zip(frames, metas), start=1):
            out = os.path.join(work, "replay%d" % i)
            shutil.rmtree(out, ignore_errors=True)
            rc, text = run([a.exe, *shlex.split(args, posix=False), *paths], out)
            check("frame %d: its own command runs" % i, rc == 0,
                  "exit %s: %s" % (rc, text.strip().splitlines()[-1][:140] if text.strip() else ""))
            got = pngs(out) if os.path.isdir(out) else []
            check("frame %d: it renders exactly one frame" % i, len(got) == 1,
                  repr([os.path.basename(g) for g in got]))
            if len(got) == 1:
                r = RE.pixels_differ(frame, got[0])
                check("frame %d: the frame it renders is this one, pixel for pixel" % i,
                      r.get("sameShape") and r.get("different") == 0, json.dumps(r))
            elif got:
                best = min(RE.pixels_differ(frame, g).get("different") or 1 << 30 for g in got)
                check("frame %d: the frame it renders is this one, pixel for pixel" % i, False,
                      "closest of %d frames differs in %s pixels" % (len(got), best))
    finally:
        os.replace(moved, wl)

    # ---- the report block beside each frame says the same thing -------------------
    sides = sorted(f for f in os.listdir(base) if f.endswith("-configuration.json"))
    said = []
    for side in sides:
        doc = json.load(open(os.path.join(base, side), encoding="utf-8"))
        view = doc.get("View") or {}
        ra = view.get("replayArgs")
        owner = side.replace("-configuration.json", "")
        mine = [m[0] for f, m in zip(frames, metas) if os.path.basename(f).startswith(owner)]
        check("report %s: its identity block carries a command" % owner[:44], bool(ra), repr(ra))
        check("report %s: that command is this shot's, the same one its frame carries" % owner[:44],
              bool(mine) and ra == mine[0], (ra or "")[:110])
        said.append(ra)
    # The pair check, so the two above cannot both pass by everything being identical.
    check("the report blocks of two different shots name different commands",
          len(said) == 2 and said[0] != said[1],
          "both say: %s" % ((said[0] or "")[:110] if said else "nothing"))

    # ---- the planes report is a report too ---------------------------------------
    reports = sorted(f for f in os.listdir(base) if f.endswith("-planes.json"))
    check("the run wrote a planes report to check", len(reports) == 2,
          repr(reports) or "none written")
    named = []
    for rep in reports:
        doc = json.load(open(os.path.join(base, rep), encoding="utf-8"))
        ra = doc.get("replayArgs")
        owner = rep.replace("-planes.json", "")
        mine = [m[0] for f, m in zip(frames, metas)
                if os.path.basename(f).startswith(owner.rsplit("-fp", 1)[0])]
        check("planes report %s: names a command" % owner[:40], bool(ra), repr(ra))
        check("planes report %s: the same one its frame carries" % owner[:40],
              bool(mine) and ra == mine[0], (ra or "")[:110])
        named.append(ra)
    check("two shots' planes reports name different commands",
          len(named) == 2 and named[0] != named[1],
          "both say: %s" % ((named[0] or "")[:110] if named else "nothing"))

    # ---- the shot a command line cannot express ----------------------------------
    check_unexpressible(a, work, paths)

    print("\n%d checks failed" % len(FAILS))
    for f in FAILS:
        print("  ", f)
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
