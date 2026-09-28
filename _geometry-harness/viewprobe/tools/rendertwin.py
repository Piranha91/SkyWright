"""rendertwin - a shot rendered in a BATCH must equal the same shot rendered by a fresh process.

One renderer process can be asked for several shots (a worklist with many shots, and many cells).
That is the batch path this repository has today, and it is the path any scene reuse would be built
on. The hazard it carries is not a crash: it is a picture that is quietly wrong because something
the previous shot left behind was still standing. Exit codes cannot see that, and neither can
"nothing failed". Outputs can.

  twins     render a worklist as ONE process, then render each of its shots ALONE from its own
            one-shot worklist, and compare each batched shot with its fresh twin.
  shuffle   render the same shots in two DIFFERENT orders and compare each shot with itself. This
            is the leak detector: an answer that depends on what was rendered before it is state
            carried from one shot into the next.
  control   render one command twice, into different folders, and require equal. A suite whose
            green control comes back red is a broken suite, not a finding - so this is the cell to
            read first, and the only one whose verdict is known before it runs.
  fields    the coverage check: every property of the worklist shot record, and whether `makelist`
            varies it. A property this file does not name makes it refuse, so a new shot field
            cannot quietly become a row the leak detector never varies.
  makelist  write a worklist whose shots differ in every row a batch lets a shot differ in.
  selftest  the cutting, the pairing and the controls, on synthetic folders, in under a second.

HOW A SHOT IS CUT OUT OF A BATCH, and why the cut is not what makes two shots equal.

A batch writes every shot's files into one folder and prints every shot's console into one stream,
so a per-shot comparison has to cut both. Both cuts are made by something the renderer itself
writes, and both are enumerated here:

  files     every output file of a worklist run carries `-c<cell>s<shot>-` in its name - the shot's
            POSITION IN THE RUN (Program.cs, the `safe` name). That is the one thing a batch and a
            one-shot twin cannot share, and the one thing a shuffle deliberately changes. So the
            files of shot k are hard-linked into a folder of their own with that segment replaced
            by `--`, and the comparison runs over those folders. Which files belong to which shot
            is taken from the WORKLIST, not from the name: position (cell, shot) indexes the shot
            that was asked for. A file whose name carries no position, or a position no shot
            claims, is a LEFTOVER and is reported, never dropped.
  console   a worklist run prints `=== worklist cell i/n shot j/m: LABEL ===` before each shot
            (Program.cs:1418). A shot's block is that line and everything up to the next one; what
            comes before the first marker is the process PREAMBLE, which a batch prints once and
            each twin prints for itself, and which is compared separately as its own part. On the
            marker line itself the four counts are replaced, for the same reason the file names
            are: they are the shot's position in the run. THE LABEL IS KEPT AND COMPARED.

Neither cut can be what makes two shots equal, and the suite proves it rather than asserting it:
every arm runs a CROSS-PAIR control - shot A's slice against shot B's twin, shot A's block against
shot B's block - and requires it to come back DIFFERENT. If the cut were removing the answer along
with the position, the cross pair would come back equal and the arm refuses.

Everything else about the comparison is `renderequiv.py`'s, unchanged and imported: decoded pixels
(never file bytes), reports canonicalised by the enumerated-field rule and streamed, the console
with its enumerated run-specific fragments removed, the renderer's own census, the exit code, and
the rule that a part which was not compared is `notCompared` and has to be waived BY NAME.

Exit 0 = every shot equalled its twin and every control behaved. Exit 1 = at least one did not
(the differing parts are named). Exit 2 = the test could not be run.
"""
from __future__ import annotations

import argparse
import json
import os
import random
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.abspath(os.path.join(HERE, "..", "..", "..", "scripts")))
import renderequiv as RE  # noqa: E402

try:
    from runlog import Runner                    # the project's run records
except ImportError:                              # pragma: no cover - a checkout without scripts/
    Runner = None

# The shot's position in the run, in a file name: `<cell>-c1s2-<label slug>-fp.png`.
POSITION = re.compile(r"-c(\d+)s(\d+)-")
# The per-shot console marker the renderer prints, Program.cs:1418.
MARKER = re.compile(r"^=== worklist cell (\d+)/(\d+) shot (\d+)/(\d+): (?P<label>.*) ===\s*$")

PREAMBLE = "<preamble>"
# A preamble has no files of its own and carries no build census: the census the renderer prints is
# part of a SHOT's block. Waived by name here so the choice is recorded rather than implied.
PREAMBLE_WAIVE = ("fileSet", "frames", "reports", "masks", "census")


# ------------------------------------------------------------------ the shot record's rows
#
# Every property of HarnessShared.WorklistShot, and what `makelist` does with it. `fields` reads the
# record from the C# source and refuses if this table and that record disagree in either direction:
# a new shot property that nothing here names would otherwise become a row the leak detector never
# varies, which is the quiet way a detector stops detecting.
SHOT_ROWS = {
    "Label":      "varied: every shot's label differs, and the label decides the output file names",
    "Eye":        "varied: shots stand in different places",
    "Yaw":        "varied: the shots that aim by yaw/pitch use different ones",
    "Pitch":      "varied: as Yaw",
    "Fov":        "varied: two field-of-view angles",
    "Aspect":     "varied: one shot inherits the run's canvas shape, one asks for its own",
    "Look":       "varied: one shot aims by Look (which wins over Yaw/Pitch), the others do not",
    "FloorZ":     "varied: present on one shot, absent on another",
    "Nav":        "varied: the navmesh overlay is off on one shot and drawn on another",
    "CullAbove":  "varied: absent, and two different cut heights - measured from the LOOK point, "
                  "so the cut lands in the geometry being looked at rather than above the eye",
    "Planes":     "varied: the occlusion-plane layer is off on one shot and on for another",
    "Occlusion":  "varied: the occlusion preview mode, --occlusion-mode (off by default: on|diff "
                  "needs planes in the cell, so the caller turns it on where the cell has them)",
    "State":      "varied only with --state: a state arm names records of one load order, so there "
                  "is nothing to put here that is true of every setup",
    "Route":      "varied only with --with-route: a route makes the shot inexpressible on a command "
                  "line, so its frame keeps the worklist-based replay string - a separate arm",
    "Routes":     "varied with --with-route, as Route: one measured path, drawn through its points",
    "Props":      "varied only with --prop FORMKEY: a synthetic prop names a base record of one "
                  "load order",
    "Npcs":       "varied only with --npc FORMKEY: as Props",
    "ViewpointKind":     "varied: it is written into the planes report (Program.cs:4108)",
    "ViewpointEvidence": "varied: as ViewpointKind (Program.cs:4110)",
    "Control":    "varied: it is written into the report AND changes a count in it "
                  "(nonControlVisibleUniqueRefs, Program.cs:4120)",
    "Note":       "NOT varied: nothing on the render path reads a shot's Note (grep for `.Note` in "
                  "viewprobe/*.cs finds the CELL's note in --emit-bats and nothing else)",
    "Feet":       "NOT varied: the record's own documentation says the render ignores it, and grep "
                  "for `.Feet` in viewprobe/*.cs finds only a biped slot flag of that name",
    "Doors":      "NOT varied: the render ignores it; grep for `Doors` in viewprobe/*.cs finds no "
                  "reader at all",
}

# What the strategy's leak detector asks to be varied and a worklist shot CANNOT carry today. Not a
# gap in the shuffle: a gap in the batch path, and the sentence that says so.
RUN_LEVEL_ROWS = {
    "--size": "the frame size is a process argument; the shot record has no Size, so every shot of "
              "a batch is the same size",
    "--visibility-budget": "a process argument; no shot property",
    "--planes-reach": "a process argument; no shot property",
    "--pick": "a process argument; no shot property",
    "--sky": "a process argument; no shot property, though the water bake depends on it",
}


def shot_rows_from_source(path):
    """The record's own parameter names, read from the C# source - the population, not a guess."""
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        text = f.read()
    m = re.search(r"public sealed record WorklistShot\((?P<body>.*?)\);", text, re.S)
    if not m:
        return None
    names = []
    for part in re.split(r",\s*(?![^<>()]*[>)])", m.group("body")):
        part = part.strip().split("=")[0].strip()
        if part:
            names.append(part.split()[-1])
    return names


def fields(source):
    """Refuse unless this file's table and the record agree in both directions."""
    got = shot_rows_from_source(source)
    if got is None:
        print("could not read the WorklistShot record from %s" % source)
        return 2
    missing = [n for n in got if n not in SHOT_ROWS]
    extra = [n for n in SHOT_ROWS if n not in got]
    for n in got:
        print("  %-20s %s" % (n, SHOT_ROWS.get(n, "*** NOT NAMED IN rendertwin.SHOT_ROWS ***")))
    print("\n  rows a batch cannot vary per shot today:")
    for n, why in RUN_LEVEL_ROWS.items():
        print("  %-20s %s" % (n, why))
    if missing or extra:
        print("\nREFUSED: the record and the table disagree."
              + ("\n  in the record, not named here: " + ", ".join(missing) if missing else "")
              + ("\n  named here, not in the record: " + ", ".join(extra) if extra else ""))
        return 1
    print("\n%d shot properties, every one named." % len(got))
    return 0


# ------------------------------------------------------------------ cutting a batch apart

def slug_of(label):
    """The renderer's own rule for the file-name slug of a shot label (Program.cs:3650)."""
    s = "".join(ch if ch.isalnum() else "_" for ch in label).strip("_")
    if len(s) > 48:
        s = s[:48].rstrip("_")
    return s


def canonical_rel(rel):
    """A relative output path with the shot's POSITION removed from every segment that has one."""
    return "/".join(POSITION.sub("--", seg, count=1) for seg in rel.split("/"))


def position_of(rel):
    m = POSITION.search(rel.split("/")[0])
    return (int(m.group(1)), int(m.group(2))) if m else None


def _link(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    try:
        os.link(src, dst)
    except OSError:
        shutil.copy2(src, dst)


def slice_run(out_dir, dest_root, keys_by_position):
    """Hard-link each output file into a folder for the shot it belongs to, with the position
    segment removed from its name. Returns ({key: folder}, [leftover relative paths])."""
    slices, leftovers = {}, []
    for root, _dirs, names in os.walk(out_dir):
        for n in names:
            src = os.path.join(root, n)
            rel = os.path.relpath(src, out_dir).replace("\\", "/")
            pos = position_of(rel)
            key = keys_by_position.get(pos) if pos else None
            if key is None:
                leftovers.append(rel)
                continue
            dest = os.path.join(dest_root, str(key).replace("/", "_"))
            slices.setdefault(key, dest)
            _link(src, os.path.join(dest, canonical_rel(rel).replace("/", os.sep)))
    return slices, sorted(leftovers)


def cut_console(text):
    """{PREAMBLE: [lines], label: [lines]} - a shot's block is its marker line and everything up to
    the next marker. The marker's four counts are the shot's position in the run and are replaced;
    the label is kept, so a block that belongs to another shot cannot compare equal."""
    blocks, current = {PREAMBLE: []}, PREAMBLE
    for ln in (text or "").splitlines():
        m = MARKER.match(ln.strip())
        if m:
            current = m.group("label")
            blocks.setdefault(current, [])
            blocks[current].append("=== worklist cell <i>/<n> shot <j>/<m>: %s ===" % current)
            continue
        blocks[current].append(ln)
    return blocks


# The KIND of a line: the line with every number replaced, so that two printings of one sentence
# are the same kind however their counts differ, and two different sentences are not. It is what
# stops the process-once rule below from removing a line whose VALUE changed - `winning state: 1,250
# refs` and `winning state: 1,251 refs` are one kind, while the two `BOS on:` lines the renderer
# prints (the layer being loaded, and the overlay's count) are two.
NUMBER = re.compile(r"\d[\d,.]*")


def kind_of(line):
    return NUMBER.sub("N", line)


def process_once(whole_run, block, other_block):
    """Lines a batch prints ONCE FOR THE PROCESS, not once per shot.

    A worklist run loads some things on its first shot and keeps them - the base-object-swap layer
    is loaded under a `bosLayer is null` guard (Program.cs:1671) and announces itself there - so the
    first shot's block carries lines every later shot's block lacks, while a fresh one-shot process
    prints them for its only shot. That is a difference in WHERE a line is printed, not in what the
    renderer answered, and a comparison that called it a difference would be red for every shot of
    every batch for ever.

    A line is removed only under both of these, and every removed line is reported by name:
      - it appears somewhere else in the run it came from, so it WAS printed, just not here;
      - THIS shot's block prints no line of the same KIND, so a line whose value changed is never
        removed - its kind is present and it stays a difference.
    Returns (the block to compare, the lines removed).
    """
    here, kinds, whole = set(block), {kind_of(ln) for ln in block}, set(whole_run)
    keep, removed = [], []
    for ln in other_block:
        if ln not in here and ln in whole and kind_of(ln) not in kinds:
            removed.append(ln)
        else:
            keep.append(ln)
    return keep, removed


def align_blocks(a_whole, a_block, b_whole, b_block):
    """The two blocks with each side's process-once lines removed from the other's, symmetrically:
    either run may be the one that printed a line on a different shot."""
    if a_block is None or b_block is None:
        return a_block, b_block, [], []
    a_s, b_s = RE.scrub_console("\n".join(a_block)), RE.scrub_console("\n".join(b_block))
    aw, bw = RE.scrub_console(a_whole), RE.scrub_console(b_whole)
    b_keep, b_removed = process_once(aw, a_s, b_s)
    a_keep, a_removed = process_once(bw, b_s, a_s)
    return a_keep, b_keep, a_removed, b_removed


def read_console(path):
    """A Runner log without the two header lines it writes itself (the argv and the elapsed)."""
    with open(path, encoding="utf-8", errors="replace") as f:
        text = f.read()
    parts = text.split("\n\n", 1)
    return parts[1] if len(parts) == 2 and parts[0].startswith("$ ") else text


# ------------------------------------------------------------------ comparing one shot

def classify(res):
    """Name the one report difference that is NOT a difference in the answer: a line holding an
    absolute path in a field the enumerated list does not name. The lines still hashed equal, so
    the content agrees; the comparator refuses anyway, which is what it is for. It is reported
    under its own name rather than being folded into `equal` - the enumerated set is a contract,
    and a fourth field is a change to that contract, not something a test may wave through."""
    rep = res["parts"].get("reports") or {}
    if (rep.get("status") == RE.DIFFERENT and not rep.get("rows")
            and not rep.get("unmatched") and rep.get("unlistedPathLines")):
        res["reportPathRefusal"] = rep["unlistedPathLines"][:4]
    return res


def compare_shot(a_dir, b_dir, a_block, b_block, a_exit, b_exit, masks, waive,
                 a_whole="", b_whole=""):
    """One shot, both sides, every part. The blocks are aligned first: what a batch prints once for
    the whole process is removed from the other side's block BY NAME and reported."""
    a_keep, b_keep, a_gone, b_gone = align_blocks(a_whole, a_block, b_whole, b_block)
    res = classify(RE.compare(a_dir, b_dir,
                              "\n".join(a_keep) if a_keep is not None else None,
                              "\n".join(b_keep) if b_keep is not None else None,
                              a_exit, b_exit, waive=waive, masks=masks))
    if a_gone or b_gone:
        res["processOnce"] = {"removedFromA": a_gone, "removedFromB": b_gone}
    return res


def statuses(res):
    return {k: v["status"] for k, v in res["parts"].items()}


def differing(res):
    return [k for k, v in res["parts"].items() if v["status"] == RE.DIFFERENT]


def why(res):
    """Why a comparison is not equal, in the two ways it can fail to be: a part that DIFFERS, and a
    part that was not compared and was not waived by name. The second is the one a reader of a
    verdict line would otherwise be left guessing at."""
    if res["equal"]:
        return ""
    bits = []
    if differing(res):
        bits.append("differ: " + ",".join(differing(res)))
    if res["notComparedAndNotWaived"]:
        bits.append("not compared and not waived: " + ",".join(res["notComparedAndNotWaived"]))
    return "  ".join(bits)


# ------------------------------------------------------------------ worklists

class WorklistRefused(ValueError):
    """A worklist this tool will not run, and why - by name."""


def _canonical_key(obj, name, where):
    """Rename the one key of `obj` that spells `name` in any letter case to `name` itself, as viewprobe's reader does
    (_shared/Worklist.cs: PropertyNameCaseInsensitive). Returns True if present. Two spellings in one object are refused:
    which one the renderer would keep is not this tool's guess."""
    hits = [k for k in obj if isinstance(k, str) and k.lower() == name.lower()]
    if len(hits) > 1:
        raise WorklistRefused("%s spells %s two ways (%s): ambiguous, refused"
                              % (where, name, ", ".join(repr(h) for h in hits)))
    if hits and hits[0] != name:
        obj[name] = obj.pop(hits[0])
    return bool(hits)


def canonical_worklist(wl, where="the worklist"):
    """The worklist with its structural keys (Cells, Cell, Shots, Label) spelled as this file reads them. Refuses, by name,
    a worklist with no cells list, a cell with no shots list, a key spelled two ways, and a worklist with 0 shots."""
    if not isinstance(wl, dict) or not _canonical_key(wl, "Cells", where) or not isinstance(wl["Cells"], list):
        raise WorklistRefused("%s has no Cells list (in any letter case): not a worklist" % where)
    _canonical_key(wl, "Label", where)
    n = 0
    for ci, cell in enumerate(wl["Cells"]):
        if not isinstance(cell, dict):
            raise WorklistRefused("%s: cell %d is not an object" % (where, ci + 1))
        _canonical_key(cell, "Cell", "%s cell %d" % (where, ci + 1))
        name = "cell %d (%s)" % (ci + 1, cell.get("Cell"))
        if not _canonical_key(cell, "Shots", "%s %s" % (where, name)) or not isinstance(cell["Shots"], list):
            raise WorklistRefused("%s: %s has no Shots list (in any letter case)" % (where, name))
        for si, shot in enumerate(cell["Shots"]):
            if isinstance(shot, dict):
                _canonical_key(shot, "Label", "%s %s shot %d" % (where, name, si + 1))
        n += len(cell["Shots"])
    if n == 0:
        raise WorklistRefused("%s has 0 shots: nothing to compare, refused" % where)
    return wl


def load_worklist(path):
    with open(path, encoding="utf-8-sig") as f:
        return canonical_worklist(json.load(f), path)


def shot_index(wl):
    """[(position, cellIndex, shotIndex, label)] for every shot, in the order the run renders them."""
    rows = []
    for ci, cell in enumerate(wl.get("Cells") or []):
        for si, shot in enumerate(cell.get("Shots") or []):
            rows.append(((ci + 1, si + 1), ci, si, shot.get("Label") or ""))
    return rows


def one_shot_worklist(wl, ci, si):
    """The same shot, alone, in a file of its own: everything else about the worklist kept."""
    cell = wl["Cells"][ci]
    out = {k: v for k, v in wl.items() if k != "Cells"}
    out["Cells"] = [{**{k: v for k, v in cell.items() if k != "Shots"},
                     "Shots": [cell["Shots"][si]]}]
    return out


def labels_unique(wl):
    seen = [lb for _p, _c, _s, lb in shot_index(wl)]
    return len(set(seen)) == len(seen), seen


# ------------------------------------------------------------------ running the renderer

def foreign_renderer_running(exe):
    try:
        out = subprocess.run(["tasklist", "/FO", "CSV", "/NH"], capture_output=True,
                             text=True).stdout.lower()
    except OSError:
        return False
    return os.path.basename(exe).lower() in out


class Renders:
    """Every render of an arm, through the shared Runner: one parent run, one child per render,
    each child's console kept on disk beside its output."""

    def __init__(self, exe, work, tail):
        self.exe, self.work, self.tail = exe, work, tail
        self.logs = os.path.join(work, "console")
        self.jobs = []

    def add(self, tag, worklist_path, out_dir):
        shutil.rmtree(out_dir, ignore_errors=True)
        os.makedirs(out_dir, exist_ok=True)
        self.jobs.append({"id": tag,
                          "argv": [self.exe, worklist_path, *self.tail, "--out", out_dir],
                          "features": {"shots": (1, "shots")}})
        return {"tag": tag, "out": out_dir, "log": os.path.join(self.logs, tag + ".txt")}

    def run(self):
        if Runner is None:
            raise SystemExit("scripts/runlog.py is not importable: refusing to render without records")
        results = Runner("rendertwin", out_dir=self.logs,
                         session_id=os.environ.get("HOUSECARL_SESSION_ID")).run(self.jobs)
        self.jobs = []
        return {r["id"]: r for r in results}


def preamble_part(a_lines, b_lines, a_exit, b_exit, waive, work, expected=None):
    """The process preamble - everything a run prints before its first shot marker - as its own
    part, with the lines that differ named. It is compared, never dropped: the batch's start-up and
    a fresh process's start-up are the same work unless something has changed.

    `expected` says why a difference here is not a shot's difference. A batch and a ONE-SHOT twin
    are different jobs and their preambles say so ("3 shot(s)" against "1 shot(s)"), so in that arm
    the difference is recorded and named rather than counted against the shots. Where both sides
    are the same shape - the control, the shuffle - nothing is expected and a difference counts."""
    res = classify(RE.compare(os.path.join(work, "empty-a"), os.path.join(work, "empty-b"),
                              "\n".join(a_lines), "\n".join(b_lines), a_exit, b_exit,
                              waive=tuple(waive) + PREAMBLE_WAIVE))
    out = outcome("preamble", res, [a_exit, b_exit])
    xa, xb = RE.scrub_console("\n".join(a_lines)), RE.scrub_console("\n".join(b_lines))
    out["onlyInA"] = [ln for ln in xa if ln not in xb][:10]
    out["onlyInB"] = [ln for ln in xb if ln not in xa][:10]
    out["expectedDifference"] = expected
    if not res["equal"]:
        print("  preamble: DIFFERENT%s  %d line(s) only in the first, %d only in the second"
              % ((" (expected: " + expected + ")") if expected else "",
                 len(out["onlyInA"]), len(out["onlyInB"])))
    return out


def outcome(tag, res, exits):
    return {"shot": tag, "equal": res["equal"], "parts": statuses(res),
            "notEqual": differing(res), "exits": exits,
            "processOnce": res.get("processOnce"),
            "reportPathRefusal": res.get("reportPathRefusal"),
            "notComparedAndNotWaived": res["notComparedAndNotWaived"]}


# ------------------------------------------------------------------ the arms

def arm_control(a, tail):
    """Two fresh runs of ONE command into different folders. Known green; read it first."""
    wl = load_worklist(a.worklist)
    ok, seen = labels_unique(wl)
    if not ok:
        print("refusing: two shots share a label, so their files and console blocks cannot be told "
              "apart: %s" % seen)
        return 2, {"verdict": "duplicate shot labels"}
    r = Renders(a.exe, a.work, tail)
    one = r.add("control-1", a.worklist, os.path.join(a.work, "c1"))
    two = r.add("control-2", a.worklist, os.path.join(a.work, "c2"))
    res = r.run()
    doc = {"arm": "control", "exits": {k: v["exitCode"] for k, v in res.items()}}
    if any(v["exitCode"] != 0 for v in res.values()):
        doc["verdict"] = "a control render failed; nothing can be compared with it"
        print(doc["verdict"] + " " + json.dumps(doc["exits"]))
        return 2, doc

    whole = classify(RE.compare(one["out"], two["out"],
                                read_console(one["log"]), read_console(two["log"]),
                                res["control-1"]["exitCode"], res["control-2"]["exitCode"],
                                waive=a.waive, masks=a.masks))
    doc["wholeRun"] = outcome("whole run", whole, [0, 0])
    print("whole run: %s  %s  %s" % ("EQUAL" if whole["equal"] else "DIFFERENT",
                                     json.dumps(statuses(whole)), why(whole)))

    # The same pair again, but through the per-shot cut - so the cut is exercised on a pair whose
    # answer is already known, before any arm that depends on it is believed.
    rows = shot_index(wl)
    keys = {pos: lb for pos, _ci, _si, lb in rows}
    sa, la = slice_run(one["out"], os.path.join(a.work, "slice-c1"), keys)
    sb, lb_ = slice_run(two["out"], os.path.join(a.work, "slice-c2"), keys)
    ca, cb = read_console(one["log"]), read_console(two["log"])
    ba, bb = cut_console(ca), cut_console(cb)
    doc["leftovers"] = {"control-1": la, "control-2": lb_}
    doc["preamble"] = preamble_part(ba[PREAMBLE], bb[PREAMBLE], 0, 0, a.waive, a.work)
    doc["shots"] = []
    for _pos, _ci, _si, label in rows:
        res_s = compare_shot(sa.get(label), sb.get(label), ba.get(label), bb.get(label),
                             0, 0, a.masks, a.waive, ca, cb)
        doc["shots"].append(outcome(label, res_s, [0, 0]))
        print("  shot %-28s %s %s" % (label[:28], "EQUAL" if res_s["equal"] else "DIFFERENT",
                                      why(res_s)))
    doc["crossPair"] = cross_pair(rows, sa, sb, ba, bb, a, ca, cb)
    doc["processOnceRemovals"] = sum(len((s.get("processOnce") or {}).get("removedFromA", []))
                                     + len((s.get("processOnce") or {}).get("removedFromB", []))
                                     for s in doc["shots"])
    print("  process-once lines removed in this arm: %d (a control: two runs of ONE command have "
          "the same shape, so nothing should be removed here)" % doc["processOnceRemovals"])
    bad = [s for s in doc["shots"] if not s["equal"]] + ([] if doc["wholeRun"]["equal"] else [1])
    if doc["processOnceRemovals"] or not doc["preamble"]["equal"]:
        bad.append(1)
    doc["verdict"] = ("the control is green: two fresh runs of one command agree, whole and shot by shot"
                      if not bad and doc["crossPair"]["ok"] else
                      "THE CONTROL IS RED - fix the suite before reading any other arm")
    print(doc["verdict"])
    return (0 if not bad and doc["crossPair"]["ok"] else 1), doc


def cross_pair(rows, sa, sb, ba, bb, a, a_whole="", b_whole=""):
    """Shot i of one run against shot j of the other: it must come back DIFFERENT, or the cut and
    the renaming are what is making shots equal and no other verdict in this arm means anything."""
    labels = [lb for _p, _c, _s, lb in rows]
    if len(labels) < 2:
        return {"ok": False, "why": "a cross pair needs two shots; this worklist has one"}
    pairs = []
    for i, j in ((0, 1), (1, 0)):
        x, y = labels[i], labels[j]
        if sa.get(x) is None or sb.get(y) is None:
            continue
        res = compare_shot(sa[x], sb[y], ba.get(x), bb.get(y), 0, 0, a.masks, a.waive,
                           a_whole, b_whole)
        pairs.append({"a": x, "b": y, "equal": res["equal"], "differing": differing(res),
                      "pixelsDiffer": "frames" in differing(res),
                      "notComparedAndNotWaived": res["notComparedAndNotWaived"]})
    # Not merely "not equal": the DECODED PIXELS have to differ. Two shots whose file names differ
    # would be unequal on the name alone, and a name is not an answer.
    ok = bool(pairs) and all(p["pixelsDiffer"] for p in pairs)
    print("  cross pair (the control on the cut itself): %s"
          % ("different pixels, as it must be" if ok
             else "NOT different in pixels - the cut is hiding the answer"))
    return {"ok": ok, "pairs": pairs,
            "why": "shot A's files and console block against shot B's; equal here would mean the "
                   "cut removed the answer along with the position"}


def arm_twins(a, tail):
    """One batch, then every shot of it alone, and each shot against its own fresh twin."""
    wl = load_worklist(a.worklist)
    ok, seen = labels_unique(wl)
    if not ok:
        print("refusing: two shots share a label: %s" % seen)
        return 2, {"verdict": "duplicate shot labels"}
    rows = shot_index(wl)
    r = Renders(a.exe, a.work, tail)
    batch = r.add("batch", a.worklist, os.path.join(a.work, "batch"))
    twins = []
    for n, (_pos, ci, si, label) in enumerate(rows, start=1):
        path = os.path.join(a.work, "twin%02d.json" % n)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(one_shot_worklist(wl, ci, si), f, indent=1)
        twins.append((label, r.add("twin%02d" % n, path, os.path.join(a.work, "twin%02d" % n))))
    res = r.run()

    doc = {"arm": "twins", "exits": {k: v["exitCode"] for k, v in res.items()}, "shots": []}
    if res["batch"]["exitCode"] != 0:
        doc["verdict"] = "the batch render failed; there is nothing to compare"
        print(doc["verdict"] + " " + json.dumps(doc["exits"]))
        return 2, doc
    keys = {pos: lb for pos, _ci, _si, lb in rows}
    sb_, leftover = slice_run(batch["out"], os.path.join(a.work, "slice-batch"), keys)
    cb = read_console(batch["log"])
    bb = cut_console(cb)
    doc["leftovers"] = leftover
    twin_slices, twin_blocks, twin_console = {}, {}, {}
    for label, t in twins:
        one = {(1, 1): label}
        s, lo = slice_run(t["out"], os.path.join(a.work, "slice-" + t["tag"]), one)
        twin_slices[label] = s.get(label)
        twin_console[label] = read_console(t["log"])
        twin_blocks[label] = cut_console(twin_console[label]).get(label)
        doc.setdefault("twinLeftovers", {})[label] = lo
        if twin_blocks[label] is None:
            doc.setdefault("twinNoMarker", []).append(label)

    # The preamble: a batch prints it once, each twin prints its own. Compared, not dropped.
    first_twin_log = twins[0][1]["log"]
    doc["preamble"] = preamble_part(
        bb[PREAMBLE], cut_console(read_console(first_twin_log))[PREAMBLE],
        res["batch"]["exitCode"], res[twins[0][1]["tag"]]["exitCode"], a.waive, a.work,
        expected="a batch and a one-shot twin are different JOBS and the preamble says so - the "
                 "shot count, and the line about what the run is going to do")

    for label, t in twins:
        code = res[t["tag"]]["exitCode"]
        s = compare_shot(sb_.get(label), twin_slices.get(label), bb.get(label),
                         twin_blocks.get(label), res["batch"]["exitCode"], code, a.masks, a.waive,
                         cb, twin_console.get(label, ""))
        doc["shots"].append(outcome(label, s, [res["batch"]["exitCode"], code]))
        print("  %-30s %s %s" % (label[:30], "EQUAL" if s["equal"] else "DIFFERENT", why(s)))
    doc["crossPair"] = cross_pair(rows, sb_, twin_slices, bb, twin_blocks, a, cb,
                                  twin_console.get(rows[1][3], ""))
    bad = [s for s in doc["shots"] if not s["equal"]]
    doc["verdict"] = ("every batched shot equals its fresh twin" if not bad else
                      "%d of %d batched shots differ from their fresh twin"
                      % (len(bad), len(doc["shots"])))
    print(doc["verdict"])
    return (0 if not bad and doc["crossPair"]["ok"] else 1), doc


def arm_shuffle(a, tail):
    """The same shots, in two orders. A shot whose answer changes with its neighbours is a leak."""
    wl = load_worklist(a.worklist)
    ok, seen = labels_unique(wl)
    if not ok:
        print("refusing: two shots share a label: %s" % seen)
        return 2, {"verdict": "duplicate shot labels"}
    rows = shot_index(wl)
    if len(rows) < 2:
        print("refusing: a shuffle needs at least two shots")
        return 2, {"verdict": "one shot"}
    # A permutation that happens to be the identity would test nothing, so the seed is stepped on
    # until the order really changes, and the seed that was used is recorded.
    seed_used, shuffled, order = a.seed, None, None
    for seed_used in range(a.seed, a.seed + 50):
        shuffled, order = shuffle_worklist(wl, seed_used)
        if order is not None:
            break
    if order is None:
        print("refusing: no seed from %d on permuted these shots" % a.seed)
        return 2, {"verdict": "no permutation"}
    path = os.path.join(a.work, "shuffled.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(shuffled, f, indent=1)

    r = Renders(a.exe, a.work, tail)
    one = r.add("order-a", a.worklist, os.path.join(a.work, "oa"))
    two = r.add("order-b", path, os.path.join(a.work, "ob"))
    res = r.run()
    doc = {"arm": "shuffle", "seed": seed_used, "order": order,
           "exits": {k: v["exitCode"] for k, v in res.items()}, "shots": []}
    if any(v["exitCode"] != 0 for v in res.values()):
        doc["verdict"] = "a render failed; there is nothing to compare"
        print(doc["verdict"] + " " + json.dumps(doc["exits"]))
        return 2, doc

    keys_a = {pos: lb for pos, _ci, _si, lb in rows}
    keys_b = {pos: lb for pos, _ci, _si, lb in shot_index(shuffled)}
    sa, la = slice_run(one["out"], os.path.join(a.work, "slice-oa"), keys_a)
    sb_, lb_ = slice_run(two["out"], os.path.join(a.work, "slice-ob"), keys_b)
    ca, cb = read_console(one["log"]), read_console(two["log"])
    ba, bb = cut_console(ca), cut_console(cb)
    doc["leftovers"] = {"order-a": la, "order-b": lb_}
    doc["preamble"] = preamble_part(ba[PREAMBLE], bb[PREAMBLE], 0, 0, a.waive, a.work)
    for _pos, _ci, _si, label in rows:
        s = compare_shot(sa.get(label), sb_.get(label), ba.get(label), bb.get(label),
                         0, 0, a.masks, a.waive, ca, cb)
        doc["shots"].append(outcome(label, s, [0, 0]))
        print("  %-30s %s %s" % (label[:30], "EQUAL" if s["equal"] else "DIFFERENT", why(s)))
    doc["crossPair"] = cross_pair(rows, sa, sb_, ba, bb, a, ca, cb)
    bad = [s for s in doc["shots"] if not s["equal"]]
    if not doc["preamble"]["equal"]:
        bad.append({"shot": "<preamble>", "equal": False})
    doc["verdict"] = ("no shot's answer depends on what was rendered before it" if not bad else
                      "%d of %d shots answer differently in a different order - state is carried "
                      "between shots" % (len(bad), len(doc["shots"])))
    print(doc["verdict"])
    return (0 if not bad and doc["crossPair"]["ok"] else 1), doc


def shuffle_worklist(wl, seed):
    """A new worklist with the cells and the shots inside each cell permuted. Returns (worklist,
    the order as [(cell, shot)] positions) - or (wl, None) if the permutation is the identity."""
    rnd = random.Random(seed)
    cells = list(enumerate(wl.get("Cells") or []))
    rnd.shuffle(cells)
    out, order = [], []
    for ci, cell in cells:
        shots = list(enumerate(cell.get("Shots") or []))
        rnd.shuffle(shots)
        order += [[ci + 1, si + 1] for si, _s in shots]
        out.append({**{k: v for k, v in cell.items() if k != "Shots"},
                    "Shots": [s for _si, s in shots]})
    original = [[ci + 1, si + 1] for ci, cell in enumerate(wl.get("Cells") or [])
                for si, _s in enumerate(cell.get("Shots") or [])]
    return ({**{k: v for k, v in wl.items() if k != "Cells"}, "Cells": out},
            None if order == original else order)


# ------------------------------------------------------------------ makelist

def makelist(a):
    """A worklist whose shots differ in every row a batch lets a shot differ in (SHOT_ROWS)."""
    eye = [float(x) for x in a.eye.split(",")]
    look = [float(x) for x in a.look.split(",")] if a.look else [eye[0], eye[1] + 300.0, eye[2] - 60.0]
    cells, varied = [], set()
    for ci, cell in enumerate([c for c in [a.cell, a.cell2] if c]):
        base = eye if ci == 0 else ([float(x) for x in a.eye2.split(",")] if a.eye2 else eye)
        shots = [
            {"Label": "%d a look aim, no cut" % (ci + 1), "Eye": base, "Look": look, "Fov": 65.0,
             "Nav": "off", "ViewpointKind": "diagnostic", "Control": False},
            {"Label": "%d a yaw aim, cut low" % (ci + 1),
             "Eye": [base[0] + 40.0, base[1] - 40.0, base[2] + 10.0], "Yaw": 135.0, "Pitch": 12.0,
             "Fov": 80.0, "Aspect": 1.7777778, "CullAbove": look[2] + 200.0, "Nav": "off",
             "FloorZ": base[2] - 120.0, "ViewpointKind": "control",
             "ViewpointEvidence": "the second row of the coverage table", "Control": True},
            {"Label": "%d turned around, cut high, planes on" % (ci + 1),
             "Eye": [base[0] - 60.0, base[1] + 30.0, base[2] - 20.0], "Yaw": 300.0, "Pitch": -8.0,
             "Fov": 50.0, "CullAbove": look[2] + 600.0, "Nav": a.nav_on, "Planes": True,
             "ViewpointKind": "diagnostic", "Control": False},
        ]
        varied |= {"Label", "Eye", "Yaw", "Pitch", "Fov", "Aspect", "Look", "FloorZ", "Nav",
                   "CullAbove", "Planes", "ViewpointKind", "ViewpointEvidence", "Control"}
        if a.occlusion:
            shots.append({"Label": "%d the occlusion preview" % (ci + 1),
                          "Eye": [base[0] + 10.0, base[1] + 10.0, base[2]], "Yaw": 45.0,
                          "Fov": 70.0, "Nav": "off", "Occlusion": a.occlusion})
            varied.add("Occlusion")
        if a.state:
            shots.append({"Label": "%d a state arm" % (ci + 1), "Eye": base, "Look": look,
                          "Nav": "off", "State": a.state})
            varied.add("State")
        if a.with_route:
            shots.append({"Label": "%d a route and a measured path" % (ci + 1), "Eye": base,
                          "Look": look, "Nav": "off",
                          "Route": [[base[0], base[1] + 100.0], [base[0] + 120.0, base[1] + 100.0]],
                          "Routes": [{"Points": [[base[0], base[1] - 100.0, base[2] - 110.0],
                                                 [base[0] + 90.0, base[1] - 100.0, base[2] - 110.0]],
                                      "Color": "cyan", "Label": "measured"}]})
            varied |= {"Route", "Routes"}
        if a.prop:
            shots.append({"Label": "%d a synthetic prop" % (ci + 1), "Eye": base, "Look": look,
                          "Nav": "off",
                          "Props": [{"Form": a.prop, "Pos": [look[0], look[1], look[2]],
                                     "Yaw": 30.0, "Scale": 1.0, "Label": "prop"}]})
            varied.add("Props")
        if a.npc:
            shots.append({"Label": "%d an actor" % (ci + 1), "Eye": base, "Look": look,
                          "Nav": "off", "Npcs": [{"Form": a.npc, "Label": "actor"}]})
            varied.add("Npcs")
        # LAST, and deliberately bare: a shot that overrides nothing and so INHERITS the run's own
        # settings. Inheriting is what a leak looks like - a shot that states a cut height is
        # immune to a leaked cut height, and a worklist all of whose shots state everything cannot
        # show one. This shot follows two that DO state a cut, so a reset that goes missing lands
        # here and nowhere else.
        shots.append({"Label": "%d inherits everything, after the cuts" % (ci + 1),
                      "Eye": [base[0] + 5.0, base[1] - 5.0, base[2]], "Look": look})
        cells.append({"Cell": cell, "Shots": shots})
    doc = {"Label": "rendertwin equivalence", "Cells": cells, "EyeHeight": 120.0}
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    not_varied = sorted(set(SHOT_ROWS) - varied)
    print("wrote %s: %d cell(s), %d shot(s)"
          % (a.out, len(cells), sum(len(c["Shots"]) for c in cells)))
    print("  varied:     " + ", ".join(sorted(varied)))
    print("  not varied: " + ", ".join(not_varied))
    for n in not_varied:
        print("     %-18s %s" % (n, SHOT_ROWS[n]))
    return 0


# ------------------------------------------------------------------ self-test

def _mk(dirpath, rel_to_colour, report_paths=None):
    from PIL import Image
    for rel, colour in rel_to_colour.items():
        p = os.path.join(dirpath, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        Image.new("RGBA", (8, 8), colour).save(p)
    for rel, doc in (report_paths or {}).items():
        p = os.path.join(dirpath, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=1)
    return dirpath


def selftest(tmp):
    fails = []

    def check(name, cond, detail=""):
        print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))
        if not cond:
            fails.append(name)

    check("the position segment is found and removed",
          canonical_rel("MyCell-c1s2-look-fp.png") == "MyCell--look-fp.png"
          and position_of("MyCell-c1s2-look-fp.png") == (1, 2),
          canonical_rel("MyCell-c1s2-look-fp.png"))
    check("...and a shot at another position canonicalises to the same name",
          canonical_rel("MyCell-c1s2-look-fp.png") == canonical_rel("MyCell-c3s7-look-fp.png"))
    check("...inside a folder name too, so an evidence folder slices with its frame",
          canonical_rel("MyCell-c2s3-look-fp-audit-evidence/shape-1.png")
          == "MyCell--look-fp-audit-evidence/shape-1.png",
          canonical_rel("MyCell-c2s3-look-fp-audit-evidence/shape-1.png"))
    check("a file with no position is a leftover, not another shot's",
          position_of("run-summary.json") is None)
    check("the slug rule is the renderer's",
          slug_of("1 a look aim, no cut") == "1_a_look_aim__no_cut"
          and slug_of("x" * 60) == "x" * 48)

    con = ("configuration: candidate - 8 plugins (hashed in 0.3s)\n"
           "=== worklist cell 1/1 shot 1/2: one ===\n"
           "   textured: 10 shapes (10 with a real diffuse), 0 refs without a resolvable mesh\n"
           "=== worklist cell 1/1 shot 2/2: two ===\n"
           "   textured: 11 shapes (11 with a real diffuse), 0 refs without a resolvable mesh\n")
    blocks = cut_console(con)
    check("the console cuts into a preamble and one block per shot",
          set(blocks) == {PREAMBLE, "one", "two"} and len(blocks["one"]) == 2,
          json.dumps({k: len(v) for k, v in blocks.items()}))
    check("the marker's counts are replaced and its LABEL is kept",
          blocks["one"][0] == "=== worklist cell <i>/<n> shot <j>/<m>: one ===")
    twin_one = cut_console("configuration: candidate - 8 plugins (hashed in 0.4s)\n"
                           "=== worklist cell 1/1 shot 1/1: one ===\n"
                           "   textured: 10 shapes (10 with a real diffuse), 0 refs without a "
                           "resolvable mesh\n")
    twin = cut_console("configuration: candidate - 8 plugins (hashed in 0.4s)\n"
                       "=== worklist cell 1/1 shot 1/1: two ===\n"
                       "   textured: 11 shapes (11 with a real diffuse), 0 refs without a "
                       "resolvable mesh\n")
    check("a batched shot's block and its twin's block agree once the counts are gone",
          RE.scrub_console("\n".join(blocks["two"])) == RE.scrub_console("\n".join(twin["two"])))
    check("RED: a block cut for one shot does not match another shot's",
          RE.scrub_console("\n".join(blocks["one"])) != RE.scrub_console("\n".join(twin["two"])))
    check("RED: two shots that differ only in their LABEL are still different",
          RE.scrub_console("=== worklist cell <i>/<n> shot <j>/<m>: one ===")
          != RE.scrub_console("=== worklist cell <i>/<n> shot <j>/<m>: two ==="))

    # Slicing: a two-shot batch against two one-shot twins, one of which is wrong.
    batch = _mk(os.path.join(tmp, "batch"),
                {"C-c1s1-one-fp.png": (10, 20, 30, 255), "C-c1s2-two-fp.png": (40, 50, 60, 255)},
                {"C-c1s1-one-fp-planes.json": {"image": r"C:\a\one.png", "n": 1},
                 "C-c1s2-two-fp-planes.json": {"image": r"C:\a\two.png", "n": 2}})
    t1 = _mk(os.path.join(tmp, "t1"), {"C-c1s1-one-fp.png": (10, 20, 30, 255)},
             {"C-c1s1-one-fp-planes.json": {"image": r"D:\b\one.png", "n": 1}})
    t2 = _mk(os.path.join(tmp, "t2"), {"C-c1s1-two-fp.png": (40, 50, 61, 255)},
             {"C-c1s1-two-fp-planes.json": {"image": r"D:\b\two.png", "n": 2}})
    sb_, leftover = slice_run(batch, os.path.join(tmp, "sb"), {(1, 1): "one", (1, 2): "two"})
    s1, _ = slice_run(t1, os.path.join(tmp, "s1"), {(1, 1): "one"})
    s2, _ = slice_run(t2, os.path.join(tmp, "s2"), {(1, 1): "two"})
    check("a batch slices into one folder per shot, with nothing left over",
          set(sb_) == {"one", "two"} and not leftover, json.dumps(leftover))
    W = ("masks",)
    good = compare_shot(sb_["one"], s1["one"], blocks["one"], twin_one["one"], 0, 0, False, W)
    check("GREEN: a batched shot equals its twin, every part of it",
          good["equal"], json.dumps(statuses(good)))
    bad = compare_shot(sb_["two"], s2["two"], blocks["two"], twin["two"], 0, 0, False, W)
    check("RED: a twin whose pixels differ is caught through the same rename",
          bad["parts"]["frames"]["status"] == RE.DIFFERENT)
    cross = compare_shot(sb_["one"], s2["two"], blocks["one"], twin["two"], 0, 0, False, W)
    check("RED (the cross pair): shot one's slice against shot two's twin is NOT equal - the "
          "rename is not what makes two shots agree", not cross["equal"])

    # A leftover file is reported, never silently dropped.
    _mk(os.path.join(tmp, "batch"), {"run-summary-fp.png": (1, 1, 1, 255)})
    _sb, leftover2 = slice_run(batch, os.path.join(tmp, "sb2"), {(1, 1): "one", (1, 2): "two"})
    check("RED: a file that belongs to no shot is reported as a leftover",
          leftover2 == ["run-summary-fp.png"], json.dumps(leftover2))

    # The command field, and the refusal that is still a refusal. A batched shot and its twin are
    # rendered from different worklist files by construction - the twin's is written for it - so
    # their replay commands hold different paths and the same answer.
    pa = _mk(os.path.join(tmp, "pa"), {"C-c1s1-one-fp.png": (10, 20, 30, 255)},
             {"C-c1s1-one-fp-planes.json": {"image": r"C:\a\one.png",
                                            "replayArgs": r"C:\w\list.json --size 512"}})
    pb = _mk(os.path.join(tmp, "pb"), {"C-c1s1-one-fp.png": (10, 20, 30, 255)},
             {"C-c1s1-one-fp-planes.json": {"image": r"D:\b\one.png",
                                            "replayArgs": r"D:\w\twin01.json --size 512"}})
    spa, _ = slice_run(pa, os.path.join(tmp, "spa"), {(1, 1): "one"})
    spb, _ = slice_run(pb, os.path.join(tmp, "spb"), {(1, 1): "one"})
    pr = compare_shot(spa["one"], spb["one"], blocks["one"], twin_one["one"], 0, 0, False, W)
    check("GREEN: a batched shot and its twin name the same command from two worklist files",
          pr["equal"] and not pr.get("reportPathRefusal"), json.dumps(statuses(pr)))

    pc = _mk(os.path.join(tmp, "pc"), {"C-c1s1-one-fp.png": (10, 20, 30, 255)},
             {"C-c1s1-one-fp-planes.json": {"image": r"C:\a\one.png",
                                            "replayArgs": r"C:\w\list.json --size 512",
                                            "someNewField": r"C:\x\order.txt"}})
    pd = _mk(os.path.join(tmp, "pd"), {"C-c1s1-one-fp.png": (10, 20, 30, 255)},
             {"C-c1s1-one-fp-planes.json": {"image": r"D:\b\one.png",
                                            "replayArgs": r"D:\w\twin01.json --size 512",
                                            "someNewField": r"D:\y\order.txt"}})
    spc, _ = slice_run(pc, os.path.join(tmp, "spc"), {(1, 1): "one"})
    spd, _ = slice_run(pd, os.path.join(tmp, "spd"), {(1, 1): "one"})
    pr2 = compare_shot(spc["one"], spd["one"], blocks["one"], twin_one["one"], 0, 0, False, W)
    check("RED: a report field holding a path that NEITHER enumerated list names is still refused, "
          "and named", not pr2["equal"] and pr2.get("reportPathRefusal"),
          json.dumps((pr2.get("reportPathRefusal") or [])[:1]))

    # The permutation, and the refusal when it is the identity.
    wl = {"Label": "x", "Cells": [{"Cell": "A", "Shots": [{"Label": "one"}, {"Label": "two"},
                                                          {"Label": "three"}]}]}
    sh, order = shuffle_worklist(wl, 7)
    check("the shuffle permutes the shots and says the order it used",
          order is not None and [s["Label"] for s in sh["Cells"][0]["Shots"]]
          != ["one", "two", "three"], json.dumps(order))
    check("every shot survives the shuffle",
          sorted(s["Label"] for s in sh["Cells"][0]["Shots"]) == ["one", "three", "two"])
    check("a worklist with one shot cannot be permuted, and says so",
          shuffle_worklist({"Cells": [{"Cell": "A", "Shots": [{"Label": "one"}]}]}, 1)[1] is None)
    ok, seen = labels_unique({"Cells": [{"Cell": "A", "Shots": [{"Label": "x"}, {"Label": "x"}]}]})
    check("two shots sharing a label are refused, because nothing could tell their files apart",
          not ok, json.dumps(seen))

    # The process-once rule: what it removes, and the two things it must refuse to remove.
    load = "   BOS on: 0 *_SWAP.ini read from the VFS (0 lines, 0 anomalies)"
    overlay = "   BOS on: 0 drawn ref(s) carry a swapped base (0 random)"
    state251 = "   winning state: 1,251 refs, 1 navmeshes  (<elapsed>)"
    state250 = "   winning state: 1,250 refs, 1 navmeshes  (<elapsed>)"
    whole_a = ["one's block", load, overlay, state251, "two's block", overlay, state251]
    keep, gone = process_once(whole_a, [overlay, state251], [load, overlay, state251])
    check("a line the batch printed once for the PROCESS is removed from the twin's block, by name",
          keep == [overlay, state251] and gone == [load], json.dumps(gone))
    keep2, gone2 = process_once(whole_a, [overlay, state250], [load, overlay, state251])
    check("RED: a line whose VALUE changed is NOT removed - a line of its kind is in the block",
          state251 in keep2 and gone2 == [load], json.dumps(gone2))
    keep3, gone3 = process_once(whole_a, [overlay, state251],
                                ["   something this run never printed", overlay, state251])
    check("RED: a line the batch never printed anywhere is not removed either", gone3 == [],
          json.dumps(gone3))
    check("...and the two BOS lines are two kinds, so loading the layer is not mistaken for the "
          "overlay's count", kind_of(load) != kind_of(overlay))
    check("...while one sentence printed with different counts is one kind",
          kind_of(state250) == kind_of(state251))

    # The worklist's own spelling. viewprobe reads property names case-insensitively
    # (_shared/Worklist.cs, PropertyNameCaseInsensitive), so a camelCase file renders; this tool must
    # read the same file the same way, or refuse it BY NAME - never 0 shots and a crash later.
    def wl_outcome(name, doc):
        p = os.path.join(tmp, "wl-" + name + ".json")
        with open(p, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        try:
            w = load_worklist(p)
            return "read", w, [lb for _p, _c, _s, lb in shot_index(w)]
        except Exception as e:  # noqa: BLE001 - the outcome under test is "refused, and why"
            return "refused", None, "%s: %s" % (type(e).__name__, e)

    three = [{"label": "one"}, {"label": "two"}, {"label": "three"}]
    kind, wcamel, got_c = wl_outcome("camel", {"label": "x", "cells": [{"cell": "A", "shots": three}]})
    check("W1 RED: a camelCase worklist (cells/shots/label) is read: 3 shots, labels in order",
          kind == "read" and got_c == ["one", "two", "three"], json.dumps(got_c))
    kind_p, _w, got_p = wl_outcome("pascal", {"Label": "x", "Cells": [{"Cell": "A", "Shots": [
        {"Label": "one"}, {"Label": "two"}, {"Label": "three"}]}]})
    check("W2 control: the PascalCase worklist reads the same 3 shots",
          kind_p == "read" and got_p == ["one", "two", "three"], json.dumps(got_p))
    try:
        one = one_shot_worklist(wcamel, 0, 1)
        one_ok = [s.get("Label") or s.get("label") for c in one["Cells"] for s in c["Shots"]] == ["two"]
        one_d = json.dumps(one)[:120]
    except Exception as e:  # noqa: BLE001
        one_ok, one_d = False, "%s: %s" % (type(e).__name__, e)
    check("W3 RED: the one-shot twin cut from a camelCase worklist holds that one shot", one_ok, one_d)
    try:
        shc, orderc = shuffle_worklist(wcamel, 7)
        sh_ok = orderc is not None and sorted(
            s.get("Label") or s.get("label") for c in shc["Cells"] for s in c["Shots"]) == ["one", "three", "two"]
        sh_d = json.dumps(orderc)
    except Exception as e:  # noqa: BLE001
        sh_ok, sh_d = False, "%s: %s" % (type(e).__name__, e)
    check("W4 RED: a camelCase worklist shuffles with every shot kept", sh_ok, sh_d)
    kind_n, _w, why_n = wl_outcome("neither", {"label": "x", "scenes": [{"cell": "A", "shots": three}]})
    check("W5 RED: a worklist with no cells list in any spelling is refused, naming 'Cells'",
          kind_n == "refused" and "Cells" in why_n, str(why_n))
    kind_s, _w, why_s = wl_outcome("noshots", {"cells": [{"cell": "A", "views": three}]})
    check("W6 RED: a cell with no shots list in any spelling is refused, naming 'Shots' and the cell",
          kind_s == "refused" and "Shots" in why_s and "A" in why_s, str(why_s))
    kind_b, _w, why_b = wl_outcome("both", {"Cells": [{"Cell": "A", "Shots": three}],
                                            "cells": [{"cell": "B", "shots": three}]})
    check("W7 RED: a worklist spelling one key two ways is refused as ambiguous, naming both",
          kind_b == "refused" and "Cells" in why_b and "cells" in why_b, str(why_b))
    kind_z, _w, why_z = wl_outcome("zero", {"cells": [{"cell": "A", "shots": []}]})
    check("W8 RED: a worklist with 0 shots is refused by name, never run",
          kind_z == "refused" and "0 shots" in why_z, str(why_z))

    src = os.path.abspath(os.path.join(HERE, "..", "..", "_shared", "Worklist.cs"))
    got = shot_rows_from_source(src) if os.path.isfile(src) else None
    check("the shot record's properties are read from the C# source",
          bool(got) and "CullAbove" in got, ", ".join(got or [])[:90])
    check("...and every one of them is named in this file's coverage table",
          bool(got) and not [n for n in got if n not in SHOT_ROWS],
          ", ".join(n for n in (got or []) if n not in SHOT_ROWS))

    print("SELFTEST PASS" if not fails else "SELFTEST FAIL: " + ", ".join(fails))
    return 1 if fails else 0


# ------------------------------------------------------------------ main

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)

    for name in ("control", "twins", "shuffle"):
        p = sub.add_parser(name)
        p.add_argument("--exe", required=True)
        p.add_argument("--worklist", required=True)
        p.add_argument("--work", required=True, help="a directory for the outputs; it is rewritten")
        p.add_argument("--waive", default="", help="parts not to compare, BY NAME")
        p.add_argument("--no-masks", dest="masks", action="store_false",
                       help="do not compare the evidence masks (then `masks` must be waived)")
        p.add_argument("--json", default="")
        p.add_argument("--seed", type=int, default=1)
        p.add_argument("rest", nargs=argparse.REMAINDER,
                       help="-- then every viewprobe argument except the worklist and --out")

    p = sub.add_parser("makelist")
    p.add_argument("--out", required=True)
    p.add_argument("--cell", required=True)
    p.add_argument("--cell2", default=None, help="a second cell, for the cross-cell arm")
    p.add_argument("--eye", required=True, help="X,Y,Z")
    p.add_argument("--eye2", default=None, help="X,Y,Z in the second cell")
    p.add_argument("--look", default=None, help="X,Y,Z")
    p.add_argument("--nav-on", default="wire", help="the navmesh mode the varying shot uses")
    p.add_argument("--occlusion", default=None, help="on|diff, where the cell has planes")
    p.add_argument("--state", default=None, help="a --state spec, if this setup has one")
    p.add_argument("--with-route", action="store_true",
                   help="add a shot carrying a route and a measured path")
    p.add_argument("--prop", default=None, help="FormKey of a base record with a model")
    p.add_argument("--npc", default=None, help="FormKey of an ACHR or NPC_")

    p = sub.add_parser("fields")
    p.add_argument("--source", default=os.path.abspath(
        os.path.join(HERE, "..", "..", "_shared", "Worklist.cs")))

    p = sub.add_parser("selftest")
    p.add_argument("--tmp", default=None)

    a = ap.parse_args(argv)
    if a.cmd == "selftest":
        import tempfile
        return selftest(a.tmp or tempfile.mkdtemp(prefix="rendertwin-"))
    if a.cmd == "fields":
        return fields(a.source)
    if a.cmd == "makelist":
        return makelist(a)

    a.waive = tuple(w for w in a.waive.split(",") if w)
    if not a.masks and "masks" not in a.waive:
        a.waive = a.waive + ("masks",)
    tail = [x for x in a.rest if x != "--"]
    if not os.path.isfile(a.exe):
        print("no such executable: %s" % a.exe)
        return 2
    if not os.path.isfile(a.worklist):
        print("no such worklist: %s" % a.worklist)
        return 2
    try:
        load_worklist(a.worklist)
    except (WorklistRefused, ValueError) as e:
        print("refusing: %s" % e)
        return 2
    if foreign_renderer_running(a.exe):
        print("refusing: a renderer is already running, so this test cannot mean anything")
        return 2
    os.makedirs(a.work, exist_ok=True)
    for empty in ("empty-a", "empty-b"):
        os.makedirs(os.path.join(a.work, empty), exist_ok=True)
    fn = {"control": arm_control, "twins": arm_twins, "shuffle": arm_shuffle}[a.cmd]
    code, doc = fn(a, tail)
    doc["exit"] = code
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=1)
    return code


if __name__ == "__main__":
    sys.exit(main())
