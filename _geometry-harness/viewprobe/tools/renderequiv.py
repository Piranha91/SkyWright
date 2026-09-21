"""renderequiv - decide whether two viewprobe runs produced the same answer.

The question this exists for: *a render taken beside other renders must equal the same render taken
alone.* Exit codes do not answer it and neither does "nothing failed" - a process can drop a wall,
substitute a white texture for a real one and still exit 0. So the comparison is of OUTPUTS.

WHAT IS COMPARED, and the rule that governs all of it: **a part that was not supplied is NOT
COMPARED, and a comparison with an uncompared part is not "equal" unless the caller waives that part
BY NAME.** Silence is not agreement. An earlier version of this module returned "equal" for a pair
where only one side had written a report, for a pair whose second frame differed, and for a pair
with no console and no exit codes at all; every one of those is a case this suite exists to catch,
and each is now a red cell in `selftest`.

  fileSet   the set of file paths under each output folder, compared as sets. A run that died
            before writing its report, or that dropped a candidate from its evidence folder,
            differs here even if everything it did write matches.
  frames    every top-level `.png`, PAIRED BY NAME, compared as decoded pixels - never file bytes,
            because each frame carries its own argument list in a text chunk. An unmatched name is
            a difference, not something to skip.
  reports   every `-planes.json`, paired by name, compared CANONICALLY AND BY STREAMING: a
            campaign report is ~600 MB and `json.load` of two of those will not fit, so the file is
            read line by line, the enumerated run-specific fields are dropped by key, absolute
            paths are replaced, and the surviving lines are hashed. The same pass reports any OTHER
            line that contained an absolute path - which covers the whole document by construction,
            where an in-memory walk truncated at the 50th list element did not.
  masks     decoded-pixel digests of the `*-audit-evidence` masks. Off by default because it costs
            a decode per mask and a campaign view writes over a thousand; when off it is an
            uncompared part and must be waived by name, so the choice is recorded rather than
            implied.
  console   stdout and stderr, line by line, with two enumerated kinds of fragment removed, each
            for a stated reason and nothing else:
              - path fragments, because a second run writes to a different folder;
              - a parenthesised elapsed time such as `(8.7s)`, because how long the walk took is
                not part of the answer. Not a guess: the first green control run of this suite came
                back RED on nothing but the `winning state: ... (Ns)` line's elapsed figure,
                with pixels, report, census and exit code all equal. A comparator that called
                that a difference would have called every simultaneous render different.
            The pattern requires the trailing `s` inside parentheses, so a parenthesised count is still
            compared.
  census    the renderer's own counts, which are the thing a wrong render moves.
  exitCode  compared as-is.

    python renderequiv.py compare A B --json out.json
    python renderequiv.py selftest
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys

ABSOLUTE = re.compile(r"[A-Za-z]:[\\/][^\s\"]*")
# The elapsed times this renderer prints, ENUMERATED - never "any number followed by an s". Each
# entry is a duration, and a duration is not an answer; a parenthesised COUNT beside one still has
# to differ, which the self-test checks.
#   "(8.7s)"            the stage timings
#   "(hashed in 0.4s)"  the configuration line's own timing. An n=4 exterior arm moved it by a tenth
#                       of a second while its frames, its report, its file set and its census were
#                       all identical - so the suite called four correct renders different. Found
#                       2026-09-21; the same species as the green control's first red, in different
#                       words, which is why these are listed rather than matched by a pattern.
ELAPSED = re.compile(r"\((\d+(?:[.,]\d+)?)s\)")
ELAPSED_HASHED = re.compile(r"\(hashed in (\d+(?:[.,]\d+)?)s\)")

# Fields in the planes report that hold a path this run chose. Read from the report writer at fork
# d31445ae: Program.cs:4031 (image), :4041 (sceneDepth), :3969 (planes[].candidates[].visibilityMask).
# The digests beside them - sceneDepthSha256, isolatedDepthSha256, assetSha256, inputSha256 - are
# content, not paths, and stay in the comparison.
ABSOLUTE_PATH_FIELDS = ("image", "sceneDepth", "visibilityMask")
_KEY_LINE = re.compile(r'^\s*"(%s)"\s*:' % "|".join(ABSOLUTE_PATH_FIELDS))

EQUAL, DIFFERENT, NOT_COMPARED = "equal", "different", "notCompared"


# --------------------------------------------------------------------------- reports

def canonical_report(path):
    """Stream a report to a canonical digest. O(1) memory: a campaign report is ~600 MB and two of
    them will not fit in memory as objects. Returns (digest, [lines that still held an absolute
    path], line count)."""
    h = hashlib.sha256()
    unlisted, n = [], 0
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            n += 1
            if _KEY_LINE.match(line):
                continue
            scrubbed = ABSOLUTE.sub("<path>", line)
            if scrubbed != line and len(unlisted) < 20:
                unlisted.append(line.strip()[:160])
            h.update(scrubbed.encode("utf-8"))
    return h.hexdigest(), unlisted, n


# --------------------------------------------------------------------------- console

def scrub_console(text):
    """Console lines with the two enumerated run-specific fragments removed, blank lines dropped."""
    out = []
    for ln in (text or "").splitlines():
        if not ln.strip():
            continue
        ln = ABSOLUTE.sub("<path>", ln)
        ln = ELAPSED_HASHED.sub("(hashed in <elapsed>)", ln)
        ln = ELAPSED.sub("(<elapsed>)", ln)
        out.append(ln.rstrip())
    return out


def census(text):
    """The renderer's own counts. Missing keys are reported as None rather than skipped."""
    out = {}
    m = re.search(r"textured: ([\d,]+) shapes \(([\d,]+) with a real diffuse\), ([\d,]+) refs without",
                  text or "")
    out["shapes"] = m.group(1) if m else None
    out["realDiffuse"] = m.group(2) if m else None
    out["unresolvedRefs"] = m.group(3) if m else None
    m = re.search(r"could NOT find \(white fallback[^)]*\): ([\d,]+)", text or "")
    # No line at all means zero: the renderer prints it only when the count is non-zero
    # (TexturedScene.cs:2479).
    out["whiteFallback"] = m.group(1) if m else "0"
    out["sharingErrorLines"] = str((text or "").count("cannot access the file"))
    out["extractionFailedLines"] = str((text or "").count("but extraction failed"))
    out["buildFromFileErrorLines"] = str(len(re.findall(r"BuildFromFile\(", text or "")))
    return out


# --------------------------------------------------------------------------- files

def _rel_files(d):
    out = {}
    for root, _dirs, names in os.walk(d):
        for n in names:
            p = os.path.join(root, n)
            out[os.path.relpath(p, d).replace("\\", "/")] = p
    return out


def pixels_differ(a_png, b_png):
    from PIL import Image
    import numpy as np
    with Image.open(a_png) as ia, Image.open(b_png) as ib:
        a = np.asarray(ia.convert("RGBA"))
        b = np.asarray(ib.convert("RGBA"))
    if a.shape != b.shape:
        return {"sameShape": False, "shapeA": list(a.shape), "shapeB": list(b.shape),
                "different": None, "ofPixels": None, "bbox": None}
    mask = (a != b).any(axis=2)
    n = int(mask.sum())
    bbox = None
    if n:
        ys, xs = mask.nonzero()
        bbox = [int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())]
    return {"sameShape": True, "different": n, "ofPixels": int(mask.size), "bbox": bbox}


def pixel_digest(png):
    from PIL import Image
    import numpy as np
    with Image.open(png) as im:
        return hashlib.sha256(np.asarray(im.convert("RGBA")).tobytes()).hexdigest()[:16]


# --------------------------------------------------------------------------- compare

def compare(a_dir, b_dir, a_console=None, b_console=None, a_exit=None, b_exit=None,
            waive=(), masks=False):
    """Every part reported with a status of equal / different / notCompared. `equal` is true only
    when every part is `equal`; a `notCompared` part makes the verdict false unless its name is in
    `waive`, and the waiver is recorded in the result."""
    waive = set(waive)
    parts = {}
    fa, fb = _rel_files(a_dir), _rel_files(b_dir)

    only_a, only_b = sorted(set(fa) - set(fb)), sorted(set(fb) - set(fa))
    parts["fileSet"] = {"status": EQUAL if not (only_a or only_b) else DIFFERENT,
                        "onlyInA": only_a[:20], "onlyInB": only_b[:20],
                        "counts": [len(fa), len(fb)]}

    def top(names, pred):
        return sorted(n for n in names if "/" not in n and pred(n.lower()))

    frames = top(fa, lambda n: n.endswith(".png")), top(fb, lambda n: n.endswith(".png"))
    shared = sorted(set(frames[0]) & set(frames[1]))
    if not frames[0] and not frames[1]:
        parts["frames"] = {"status": NOT_COMPARED, "why": "neither run wrote a frame"}
    else:
        rows = {n: pixels_differ(fa[n], fb[n]) for n in shared}
        unmatched = sorted(set(frames[0]) ^ set(frames[1]))
        ok = bool(shared) and not unmatched and all(r.get("different") == 0 for r in rows.values())
        parts["frames"] = {"status": EQUAL if ok else DIFFERENT, "paired": len(shared),
                           "unmatched": unmatched[:20],
                           "different": {n: r for n, r in rows.items() if r.get("different") != 0}}

    reps = top(fa, lambda n: n.endswith("-planes.json")), top(fb, lambda n: n.endswith("-planes.json"))
    shared_r = sorted(set(reps[0]) & set(reps[1]))
    if not reps[0] and not reps[1]:
        parts["reports"] = {"status": NOT_COMPARED, "why": "neither run wrote a report"}
    else:
        rows, unlisted = {}, []
        for n in shared_r:
            da, ua, la = canonical_report(fa[n])
            db, ub, lb = canonical_report(fb[n])
            unlisted += ua + ub
            rows[n] = {"equal": da == db, "lines": [la, lb], "digests": [da[:16], db[:16]]}
        unmatched = sorted(set(reps[0]) ^ set(reps[1]))
        ok = (bool(shared_r) and not unmatched and not unlisted
              and all(r["equal"] for r in rows.values()))
        parts["reports"] = {"status": EQUAL if ok else DIFFERENT, "paired": len(shared_r),
                            "unmatched": unmatched[:20], "unlistedPathLines": unlisted[:10],
                            "rows": {n: r for n, r in rows.items() if not r["equal"]}}

    mask_names = sorted(n for n in set(fa) | set(fb) if "-audit-evidence/" in n and n.endswith(".png"))
    if not mask_names:
        parts["masks"] = {"status": NOT_COMPARED, "why": "no evidence masks in either run"}
    elif not masks:
        parts["masks"] = {"status": NOT_COMPARED, "count": len(mask_names),
                          "why": "mask digests are off by default: one decode per mask, and a "
                                 "campaign view writes over a thousand. Pass masks=True to compare."}
    else:
        bad = [n for n in mask_names
               if n not in fa or n not in fb or pixel_digest(fa[n]) != pixel_digest(fb[n])]
        parts["masks"] = {"status": EQUAL if not bad else DIFFERENT, "count": len(mask_names),
                          "differing": bad[:20]}

    if a_console is None or b_console is None:
        parts["console"] = {"status": NOT_COMPARED, "why": "a console was not supplied"}
        parts["census"] = {"status": NOT_COMPARED, "why": "a console was not supplied"}
    else:
        sa, sb = scrub_console(a_console), scrub_console(b_console)
        parts["console"] = {"status": EQUAL if sa == sb else DIFFERENT,
                            "firstDifference": next(
                                (i for i, (x, y) in enumerate(zip(sa, sb)) if x != y), None),
                            "lineCounts": [len(sa), len(sb)]}
        ca, cb = census(a_console), census(b_console)
        if ca["shapes"] is None and cb["shapes"] is None:
            parts["census"] = {"status": NOT_COMPARED, "a": ca, "b": cb,
                               "why": "neither console carried a build census"}
        else:
            parts["census"] = {"status": EQUAL if ca == cb else DIFFERENT, "a": ca, "b": cb}

    if a_exit is None or b_exit is None:
        parts["exitCode"] = {"status": NOT_COMPARED, "why": "an exit code was not supplied"}
    else:
        parts["exitCode"] = {"status": EQUAL if a_exit == b_exit else DIFFERENT,
                             "a": a_exit, "b": b_exit}

    waived = sorted(n for n, p in parts.items() if p["status"] == NOT_COMPARED and n in waive)
    unwaived = sorted(n for n, p in parts.items() if p["status"] == NOT_COMPARED and n not in waive)
    equal = (all(p["status"] == EQUAL for n, p in parts.items() if n not in waived)
             and not unwaived)
    return {"a": a_dir, "b": b_dir, "parts": parts, "waived": waived,
            "notComparedAndNotWaived": unwaived, "equal": equal}


# --------------------------------------------------------------------------- self-test

def _synthetic(tmp, colour, report_image_path, extra=None, report=True, second_frame=None):
    from PIL import Image
    os.makedirs(tmp, exist_ok=True)
    Image.new("RGBA", (16, 16), colour).save(os.path.join(tmp, "shot-fp.png"))
    if second_frame is not None:
        Image.new("RGBA", (16, 16), second_frame).save(os.path.join(tmp, "shot2-fp.png"))
    if report:
        doc = {"schema": "occlusion-view-research-v1", "image": report_image_path,
               "sceneDepth": report_image_path + ".depth", "inputSha256": "abc",
               "planes": [{"candidates": [{"visibilityMask": report_image_path + ".mask",
                                           "assetSha256": "def"}]}],
               "filler": [{"k": i} for i in range(80)]}
        if extra:
            doc.update(extra)
        with open(os.path.join(tmp, "shot-planes.json"), "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=1)
    return tmp


def selftest(tmp_root):
    """The comparator seen both ways. A suite whose green control is red is broken, not a finding -
    and a suite whose red cells were never seen red is not a suite."""
    fails = []

    def check(name, cond, detail=""):
        print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))
        if not cond:
            fails.append(name)

    con = ("cell X\n   textured: 10 shapes (10 with a real diffuse), 0 refs without a resolvable mesh\n"
           "wrote:\n   C:\\a\\b-fp.png\n")
    con_b = con.replace("C:\\a\\", "D:\\other\\")
    W = ("masks",)
    a = _synthetic(os.path.join(tmp_root, "green-a"), (10, 20, 30, 255), r"C:\a\b-fp.png")
    b = _synthetic(os.path.join(tmp_root, "green-b"), (10, 20, 30, 255), r"D:\other\b-fp.png")
    g = compare(a, b, con, con_b, 0, 0, waive=W)
    check("GREEN control: two runs of one command into different folders compare equal", g["equal"],
          json.dumps({k: v["status"] for k, v in g["parts"].items()}))

    g2 = compare(a, b, con + "winning state: 5 refs  (8.7s)\n",
                 con_b + "winning state: 5 refs  (7.8s)\n", 0, 0, waive=W)
    check("GREEN: a different elapsed time is not a difference in the answer", g2["equal"])
    g3 = compare(a, b, con + "   (1,732 shapes)\n", con_b + "   (1,731 shapes)\n", 0, 0, waive=W)
    check("...but a parenthesised COUNT is still compared",
          g3["parts"]["console"]["status"] == DIFFERENT)

    # The second elapsed form, both ways round. A comment claiming a rule holds for a pattern it was
    # never run against is a claim, not a test - and the first form's cells above cannot see this one.
    hashed = "configuration: candidate - 8 plugins, 19 archives (hashed in %s)\n"
    g4 = compare(a, b, con + hashed % "0.3s", con_b + hashed % "0.4s", 0, 0, waive=W)
    check("GREEN: a different 'hashed in' time is not a difference in the answer", g4["equal"])
    g5 = compare(a, b,
                 con + "configuration: candidate - 19 archives (hashed in 0.3s)\n",
                 con_b + "configuration: candidate - 18 archives (hashed in 0.4s)\n", 0, 0, waive=W)
    check("...and a COUNT on that same line still differs, duration or no duration",
          g5["parts"]["console"]["status"] == DIFFERENT)

    c = _synthetic(os.path.join(tmp_root, "red-pixels"), (10, 20, 31, 255), r"D:\other\b-fp.png")
    check("RED: one changed pixel value is caught",
          compare(a, c, con, con_b, 0, 0, waive=W)["parts"]["frames"]["status"] == DIFFERENT)

    d = _synthetic(os.path.join(tmp_root, "red-report"), (10, 20, 30, 255), r"D:\other\b-fp.png",
                   extra={"inputSha256": "CHANGED"})
    check("RED: a changed report field is caught",
          compare(a, d, con, con_b, 0, 0, waive=W)["parts"]["reports"]["status"] == DIFFERENT)
    check("RED: a changed census count is caught",
          compare(a, b, con, con_b.replace("10 shapes (10", "9 shapes (9"), 0, 0,
                  waive=W)["parts"]["census"]["status"] == DIFFERENT)
    check("RED: a changed exit code is caught",
          compare(a, b, con, con_b, 0, 3, waive=W)["parts"]["exitCode"]["status"] == DIFFERENT)
    r5 = compare(a, b, con, con_b + "   Found 'x.nif' in BSA but extraction failed: locked\n", 0, 0,
                 waive=W)
    check("RED: an extraction-failure line on one side only is caught", not r5["equal"])

    # --- the four the reviewer found (B1). Each must be red, and each was green before. ---------
    e = _synthetic(os.path.join(tmp_root, "red-noreport"), (10, 20, 30, 255), r"D:\other\b-fp.png",
                   report=False)
    r6 = compare(a, e, con, con_b, 0, 0, waive=W)
    check("RED (B1a): a report on ONE side only is a difference, not 'neither wrote one'",
          not r6["equal"] and r6["parts"]["reports"]["status"] == DIFFERENT
          and r6["parts"]["fileSet"]["status"] == DIFFERENT)

    f2a = _synthetic(os.path.join(tmp_root, "red-2f-a"), (10, 20, 30, 255), r"C:\a\b-fp.png",
                     second_frame=(1, 2, 3, 255))
    f2b = _synthetic(os.path.join(tmp_root, "red-2f-b"), (10, 20, 30, 255), r"D:\other\b-fp.png",
                     second_frame=(1, 2, 4, 255))
    r7 = compare(f2a, f2b, con, con_b, 0, 0, waive=W)
    check("RED (B1b): a SECOND frame that differs is caught, not skipped",
          not r7["equal"] and r7["parts"]["frames"]["status"] == DIFFERENT,
          json.dumps(list(r7["parts"]["frames"]["different"].keys())))

    r8 = compare(a, b)
    check("RED (B1c): no console and no exit codes is NOT COMPARED, not 'equal'",
          not r8["equal"] and set(r8["notComparedAndNotWaived"]) >= {"console", "exitCode"},
          json.dumps(r8["notComparedAndNotWaived"]))
    r8w = compare(a, b, waive=("console", "census", "exitCode", "masks"))
    check("...and the caller can waive those parts BY NAME, which is recorded",
          r8w["equal"] and "console" in r8w["waived"])

    deep = _synthetic(os.path.join(tmp_root, "red-deeppath"), (10, 20, 30, 255),
                      r"D:\other\b-fp.png",
                      extra={"filler": [{"k": i} for i in range(80)] + [{"late": r"D:\x\late.png"}]})
    r9 = compare(a, deep, con, con_b, 0, 0, waive=W)
    check("RED (B1d): an unlisted absolute path past the 50th list element makes it refuse",
          r9["parts"]["reports"]["status"] == DIFFERENT
          and r9["parts"]["reports"]["unlistedPathLines"],
          json.dumps(r9["parts"]["reports"]["unlistedPathLines"][:2]))

    print("SELFTEST PASS" if not fails else "SELFTEST FAIL: " + ", ".join(fails))
    return 1 if fails else 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("compare")
    c.add_argument("a")
    c.add_argument("b")
    c.add_argument("--a-console")
    c.add_argument("--b-console")
    c.add_argument("--a-exit", type=int)
    c.add_argument("--b-exit", type=int)
    c.add_argument("--waive", default="", help="comma-separated part names not to require")
    c.add_argument("--masks", action="store_true")
    c.add_argument("--json")
    s = sub.add_parser("selftest")
    s.add_argument("--tmp", default=None)
    a = ap.parse_args(argv)
    if a.cmd == "selftest":
        import tempfile
        return selftest(a.tmp or tempfile.mkdtemp(prefix="renderequiv-"))
    rd = lambda p: open(p, encoding="utf-8", errors="replace").read() if p else None
    res = compare(a.a, a.b, rd(a.a_console), rd(a.b_console), a.a_exit, a.b_exit,
                  waive=[w for w in a.waive.split(",") if w], masks=a.masks)
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(res, f, indent=1)
    print(json.dumps({"equal": res["equal"],
                      "parts": {k: v["status"] for k, v in res["parts"].items()},
                      "waived": res["waived"],
                      "notComparedAndNotWaived": res["notComparedAndNotWaived"]}, indent=1))
    return 0 if res["equal"] else 1


if __name__ == "__main__":
    sys.exit(main())
