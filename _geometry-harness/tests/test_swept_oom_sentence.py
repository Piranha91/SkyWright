"""Running out of memory on the swept paths ends in one sentence that says what to try (cases 1880-1885).

    python _geometry-harness/tests/test_swept_oom_sentence.py

The house rule for errors is one plain sentence: what went wrong and what to try. Before this change motionprobe `sweep` and
`deltacheck` reported `"reason": "OutOfMemoryException: Exception of type ..."` and contactprobe's swept review refused with
"overlap contact review refused: OutOfMemoryException: ...": named, but saying nothing a person could do.

The runs are real: the probe's GC heap is capped (DOTNET_GCHeapHardLimit) so that a sweep of the motion fixture's sliding box
(proofprobe --write-motion-fixture) cannot finish, and the same cap with the sentence's own advice taken must finish, so the
advice is shown to be advice and not decoration. The octree runs on one thread so the run does the same work every time.

    1880  motionprobe sweep under the cap: status error, exit 1; the reason is one sentence that says it ran out of memory,
          names --budget and --delta with this run's values, and says to run with more memory; no exception type name.
    1881  the same cap, the sentence's advice taken (--budget 64): the region builds, exit 0 (the advice works).
    1882  motionprobe deltacheck --budget 200000 --delta 0.0001 under the cap (the samples it holds do not fit): the same
          sentence rule, with those values.
    1883  contactprobe --surface --motion swept --motion-budget 200000 --motion-delta 0.0001 under the cap: exit 2; the refusal is one sentence naming --motion-budget and
          --motion-delta with this run's values and more memory; no exception type name.
    1884  the same contactprobe run: no row carries an out-of-memory text as a per-pair result (running out of memory belongs
          to the machine, not to a pair or a member).
    1885  the same cap, contactprobe with the advice taken (--motion-budget 64): exit 0 and the swept row is measured.

Probes under test come from this checkout's Release builds unless HC_OOM_HARNESS names another harness folder (a base build,
for the red run); the fixture writers come from HC_CS_FIXTURES, else this checkout. The caps can be overridden for a
calibration run with HC_OOM_CAP_SWEEP, HC_OOM_CAP_DELTA and HC_OOM_CAP_CONTACT (hexadecimal bytes).
"""

import json
import os
import re
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import test_contact_solid_rule as cs  # noqa: E402  (the shared MO2 lab)

PROBES = Path(os.environ.get("HC_OOM_HARNESS") or cs.HARNESS)
CAP_SWEEP = os.environ.get("HC_OOM_CAP_SWEEP", "0x8000000")      # 128 MiB: the sliding box's volume does not fit
CAP_DELTA = os.environ.get("HC_OOM_CAP_DELTA", "0x1000000")      # 16 MiB: 200000 held samples do not fit (40000 fit at 32 MiB)
CAP_CONTACT = os.environ.get("HC_OOM_CAP_CONTACT", "0x4000000")  # 64 MiB: the fixture order loads, 200000 samples do not fit
FAILURES, PASSED = [], []


def check(name, fn):
    try:
        fn()
    except Exception:
        FAILURES.append(name)
        print(f"FAIL  {name}")
        print("      " + traceback.format_exc().strip().replace("\n", "\n      "))
    else:
        PASSED.append(name)
        print(f"ok    {name}")


def capped(cap, extra=None):
    return dict(os.environ, DOTNET_GCHeapHardLimit=cap, **(extra or {}))


def one_sentence(text, where, flags):
    """The rejected answers: an exception type name, more than one sentence, no remedy, a remedy without this run's value."""
    assert isinstance(text, str) and text, f"{where}: no text"
    t = text.strip()
    assert "\n" not in t, f"{where}: more than one line: {t!r}"
    assert not re.search(r"\b\w+Exception\b", t), f"{where}: names an exception type instead of saying what to try: {t!r}"
    # one sentence: it ends with a full stop and no full stop, question or exclamation mark is followed by a space before that
    # (a dot inside a file name or a number is not a sentence end)
    assert t.endswith(".") and not re.search(r"[.!?]\s", t[:-1]), f"{where}: not one sentence: {t!r}"
    assert "out of memory" in t, f"{where}: does not say it ran out of memory: {t!r}"
    for f in flags:
        assert f in t, f"{where}: does not name {f!r}: {t!r}"
    assert "more memory" in t, f"{where}: does not say to run with more memory: {t!r}"


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        L = cs.Lab(tmp)
        mp = cs.exe(PROBES, "motionprobe")
        base = [mp, "--loose-root", L.loose]
        runlog = {"HOUSECARL_RUNLOG_DIR": str(tmp / "runlog")}
        out = {}

        def sweep(label, cap, *extra):
            o = tmp / f"{label}.json"
            p = cs.run([*base, "sweep", "meshes/srfix/mover.nif", "--octree-threads", "1", *extra, "--out", o], env=capped(cap, runlog))
            out[label] = (p, json.loads(o.read_text(encoding="utf-8")) if o.exists() else None)

        sweep("sweep-capped", CAP_SWEEP)
        sweep("sweep-advice", CAP_SWEEP, "--budget", "64")
        o = tmp / "delta-capped.json"
        p = cs.run([*base, "deltacheck", "meshes/srfix/mover.nif", "--budget", "200000", "--delta", "0.0001", "--out", o], env=capped(CAP_DELTA, runlog))
        out["delta-capped"] = (p, json.loads(o.read_text(encoding="utf-8")) if o.exists() else None)
        pairs = L.pairs("oom", [(cs.WL, cs.LL)])
        cp, cdocs = L.contact("contact-capped", "mover", pairs, ["--surface", "--motion", "swept", "--motion-budget", "200000",
                                                                          "--motion-delta", "0.0001"], env={"DOTNET_GCHeapHardLimit": CAP_CONTACT})
        ap, adocs = L.contact("contact-advice", "mover", pairs, ["--surface", "--motion", "swept", "--motion-budget", "64"],
                              env={"DOTNET_GCHeapHardLimit": CAP_CONTACT})

        def doc(label):
            p, d = out[label]
            assert d is not None, f"{label}: no JSON (exit {p.returncode}): {(p.stdout + p.stderr)[-600:]}"
            return p, d

        def c1880():
            p, d = doc("sweep-capped")
            assert d.get("status") == "error" and p.returncode == 1, f"the capped sweep did not fail as expected: exit {p.returncode}, {d}"
            one_sentence(d.get("reason"), "sweep under the cap", ["--budget 4096", "--delta 0.5"])

        def c1881():
            p, d = doc("sweep-advice")
            assert p.returncode == 0 and d.get("Status") == "built", f"the advice did not work: exit {p.returncode}, {str(d)[:400]}"

        def c1882():
            p, d = doc("delta-capped")
            assert d.get("status") == "error" and p.returncode == 1, f"the capped deltacheck did not fail as expected: exit {p.returncode}, {d}"
            one_sentence(d.get("reason"), "deltacheck under the cap", ["--budget 200000", "--delta 0.0001"])

        def refusal_line(p):
            lines = [ln for ln in (p.stderr or "").splitlines() if ln.strip()]
            return lines[-1] if lines else ""

        def c1883():
            assert cp.returncode == 2, f"the capped contactprobe run did not refuse (exit {cp.returncode}): {(cp.stdout + cp.stderr)[-600:]}"
            one_sentence(refusal_line(cp), "contactprobe swept review under the cap", ["--motion-budget 200000", "--motion-delta 0.0001"])

        def c1884():
            d = cdocs.get("--surface")
            text = json.dumps(d) if d is not None else ""
            assert "OutOfMemory" not in text and "out of memory" not in text.lower(), \
                f"a row carries the out-of-memory as its own result: {text[:800]}"

        def c1885():
            d = adocs.get("--surface")
            assert ap.returncode == 0 and d is not None, f"the advice did not work: exit {ap.returncode}: {(ap.stdout + ap.stderr)[-600:]}"
            ms = cs.motions(d)
            assert ms and all(not m.get("refusal") for m in ms), f"the swept row was not measured: {ms}"

        for name, fn in [("1880 motionprobe sweep out of memory: one sentence, what to try", c1880),
                         ("1881 motionprobe sweep with the advice taken builds", c1881),
                         ("1882 motionprobe deltacheck out of memory: one sentence, what to try", c1882),
                         ("1883 contactprobe swept review out of memory: one sentence, what to try", c1883),
                         ("1884 contactprobe swept review out of memory: no row takes it as its own result", c1884),
                         ("1885 contactprobe swept review with the advice taken measures the row", c1885)]:
            check(name, fn)
        if os.environ.get("HC_OOM_DUMP"):
            d = Path(os.environ["HC_OOM_DUMP"])
            d.mkdir(parents=True, exist_ok=True)
            for f in tmp.glob("*.json"):
                (d / f.name).write_bytes(f.read_bytes())
            for k, (p, _) in out.items():
                (d / f"{k}.log").write_text(f"exit {p.returncode}\n--- stdout\n{p.stdout}\n--- stderr\n{p.stderr}", encoding="utf-8")
            for k, p in L.logs.items():
                (d / f"{k}.log").write_text(f"exit {p.returncode}\n--- stdout\n{p.stdout}\n--- stderr\n{p.stderr}", encoding="utf-8")
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
