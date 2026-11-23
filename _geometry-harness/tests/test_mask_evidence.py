"""The visibility-mask evidence writer: one encode per distinct mask, and identical bytes.

A campaign view writes one PNG per assessed shape and most of them are the SAME picture. The
writer keeps a per-view cache so a repeat is written from bytes already encoded instead of being
encoded again. Two things have to hold or the saving is a silent corruption:

  * the cache key must distinguish two masks that differ ANYWHERE - the renderer keys on the
    isolated depth buffer's SHA-256, and the mask is computed from that same buffer, so a key that
    matches must mean a mask that matches;
  * a cached write must put the same bytes on disk as an uncached one, to the byte.

The mechanism is exercised through `viewprobe --mask-evidence-selftest <dir>`, which runs the real
`MaskEvidence` code over buffers it builds and prints what it measured. THIS file holds the
expectations; the entry point only reports facts.

  python _geometry-harness/tests/test_mask_evidence.py
  VIEWPROBE_EXE=... python _geometry-harness/tests/test_mask_evidence.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HARNESS = Path(__file__).resolve().parents[1]
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
VP = buildlayout.program("viewprobe")
VP = Path(os.environ.get("VIEWPROBE_EXE", str(VP)))

OK = FAILED = 0


def check(name, cond, detail=""):
    global OK, FAILED
    if cond:
        OK += 1
        print(f"  ok   {name}  {detail}")
    else:
        FAILED += 1
        print(f"  FAIL {name}  {detail}")


def selftest(work):
    env = dict(os.environ)
    env["HOUSECARL_RUNLOG_DIR"] = str(Path(work) / "runlog")
    p = subprocess.run([str(VP), "--mask-evidence-selftest", str(work)], capture_output=True,
                       text=True, encoding="utf-8", errors="replace", env=env)
    return p.returncode, p.stdout or "", p.stderr or ""


def main():
    if not VP.exists():
        print(f"FAIL: no viewprobe at {VP}")
        return 1
    work = Path(tempfile.mkdtemp(prefix="hc-mask-ev-"))
    try:
        code, out, err = selftest(work)
        # THE GATE. A build without the entry point must not be able to pass this file: it refuses,
        # and every check below is then unreachable rather than vacuously green.
        check("the build has the mask-evidence self-test entry point",
              code == 0, f"exit {code}; {(err or out).strip().splitlines()[-1][:160] if (err or out).strip() else ''}")
        if code != 0:
            return 1
        try:
            r = json.loads(out)
        except json.JSONDecodeError:
            check("the self-test prints JSON", False, out[:200])
            return 1

        # --- a repeat is not encoded again, and lands byte-identical --------------------------
        a = r["identicalBuffers"]
        check("two identical depth buffers are one encode",
              a["writes"] == 2 and a["encodes"] == 1 and a["hits"] == 1, json.dumps(a))
        check("the repeat's file is byte-identical to the first", a["filesByteEqual"] is True, json.dumps(a))
        check("a cached write equals what an uncached write puts on disk",
              a["equalToDirectSave"] is True, json.dumps(a))

        # --- ONE depth value, ONE mask pixel: the key must NOT match --------------------------
        b = r["oneDepthValueFlipsOnePixel"]
        check("one differing depth value flips exactly one mask pixel",
              b["maskPixelsDiffering"] == 1, json.dumps(b))
        check("...and the two buffers do not share a key", b["keysEqual"] is False, json.dumps(b))
        check("...so both are encoded, and the files differ",
              b["encodes"] == 2 and b["hits"] == 0 and b["filesByteEqual"] is False, json.dumps(b))
        check("...and each file still equals an uncached write",
              b["equalToDirectSave"] is True, json.dumps(b))

        # --- the key may MISS a repeat; it may never merge two different masks -----------------
        c = r["differentDepthSameMask"]
        check("two depth buffers giving the same mask are allowed to be encoded twice",
              c["encodes"] == 2 and c["filesByteEqual"] is True, json.dumps(c))

        # --- one cache per view: nothing survives a shot (stage 4 keeps the process alive) -----
        d = r["secondView"]
        check("a second view's cache starts empty",
              d["encodesInSecondView"] == d["writesInSecondView"] and d["sharedAnyEntry"] is False,
              json.dumps(d))
        check("the second view's files are byte-identical to the first view's",
              d["filesByteEqual"] is True, json.dumps(d))

        # --- the verify switch, and it has been seen to go RED ---------------------------------
        e = r["verifyOnHealthyCache"]
        check("with verification on, every hit is re-encoded and none mismatches",
              e["hits"] > 0 and e["verified"] == e["hits"] and e["mismatches"] == 0, json.dumps(e))
        f = r["verifyCatchesALyingKey"]
        check("a key that does NOT distinguish two masks is caught by verification",
              f["mismatches"] == 1, json.dumps(f))
        check("...and the file on disk is still the right one, not the cached one",
              f["fileMatchesItsOwnMask"] is True, json.dumps(f))
        check("...while the same lying key WITHOUT verification writes the wrong bytes - which is "
              "what verification is for",
              f["withoutVerifyFileIsWrong"] is True, json.dumps(f))

        # --- the summary the run prints when verification is on --------------------------------
        check("the self-test reports the size it worked at",
              r["width"] > 0 and r["height"] > 0, f"{r['width']}x{r['height']}")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    print(f"\n{OK} ok, {FAILED} failed")
    print("PASS" if FAILED == 0 else "FAILED")
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
