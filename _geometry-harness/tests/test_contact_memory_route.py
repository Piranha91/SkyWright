"""Known answers for contactprobe's memory route on a real probe run (task 2026-10-14-contactprobe-default-pass-memory-g1,
Piranha's rule 2026-11-05: fast when the memory exists, warn on contention, lean when refused or absent, never a bare failure).

    python _geometry-harness/tests/test_contact_memory_route.py [CR-16 CR-21 ...]   (no names: every case)

On PopulationFixture laid out as an MO2 instance (test_contact_reverse.py's Lab, without its reverse runs), the REAL contactprobe:
  CR-16  a GC heap hard limit below the fast route's need: the run says lean and names the limit, completes, and its emit and rows
         equal the unlimited run's (GeneratedUtc aside); the sidecar <emit>.memory.json records route, case and readings; the
         unlimited run here says fast.
  CR-17  contention (readings from HC_MEMROUTE_TEST_READINGS, said in the route line) with --memory-route ask: exit 5 before the
         host loop, one sentence naming the program holding memory and both answers; an earlier run's emit and rows at those paths
         are gone, so nothing left there can be read as this run's.
  CR-18  contention under auto: the warning names the program; lean, recorded as the unattended default in console and sidecar;
         CR-11 within it: the lean route's run-time heap limit is what the GC reports after GC.RefreshMemoryLimit.
  CR-14  an out-of-memory in the host loop (HC_CONTACT_FAULT=oom-host-loop): exit 2, one plain sentence on stderr, no stack trace,
         and no emit or rows left at the output paths (an earlier run's included); the sidecar says the run failed.
  CR-19  nothing fits (a limit below the lean need): exit 2, one sentence, no emit, no rows.
  CR-20  out of memory before the route is decided (HC_CONTACT_FAULT=oom-before-route; on the large order a 4 GiB heap limit did
         this at 37 s with a bare "Out of memory." and exit e0434352): exit 2, one sentence, no stack trace, no emit or rows.
  CR-21  start-up garbage under a heap limit: the run log's estimator reads the whole run history when the probe starts (this
         machine's was 315 MB on 2026-11-27, about 480 MiB of heap, all garbage by the time the probe takes its 64 MiB out-of-memory
         reserve). A planted 330 MB history under the 512 MiB limit, a missing --base, in workstation and in server GC: exit 2 with
         the missing-input sentence, never a bare "Out of memory." (workstation GC on the real history: bare, 10 of 10).
Each case names the wrong answer it rejects. The probe comes from this checkout's Release build unless HC_REVERSE_HARNESS names
another build folder.
"""
import json
import os
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
# test_contact_reverse.py runs its cases at import, so only its helpers (everything above its `LAB = None`) are loaded here.
_src = (HERE / "test_contact_reverse.py").read_text(encoding="utf-8")
_MARK = "\nLAB = None\n"
assert _src.count(_MARK) == 1, "test_contact_reverse.py changed shape: its helper block is no longer above one `LAB = None`"
tcr = type(sys)("tcr_helpers")
tcr.__file__ = str(HERE / "test_contact_reverse.py")
exec(compile(_src.split(_MARK)[0], tcr.__file__, "exec"), tcr.__dict__)

FAILURES, PASSED = tcr.FAILURES, tcr.PASSED
check, run, exe = tcr.check, tcr.run, tcr.exe


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


class Lab(tcr.Lab):
    """The reverse test's instance, without its eight reverse runs."""
    def __init__(self, tmp: Path):
        self._setup = True
        super().__init__(tmp)
        self._setup = False

    def probe(self, arm, extra, env=None):
        if getattr(self, "_setup", False):
            return None
        return run([exe(tcr.PROBES, "contactprobe"), "--order", self.cand[arm], "--instance", self.inst, "--game", self.game,
                    "--profile", "P"] + extra, env=dict(os.environ, **(env or {})))


LAB = None


def lab():
    global LAB
    if LAB is None:
        LAB = Lab(Path(tempfile.mkdtemp(prefix="contact-memory-route-")))
    return LAB


def outputs(name):
    d = lab().tmp / name
    d.mkdir(exist_ok=True)
    return d / "emit.json", d / "rows.json", d / "emit.json.memory.json"


def plain(r):
    return r.stdout + r.stderr


def no_stack(r):
    assert "   at " not in plain(r) and "Unhandled exception" not in plain(r), f"a stack trace reached the user: {plain(r)[-1200:]}"


def stale(*paths):
    for p in paths:
        p.write_text('{"stale": "an earlier run"}', encoding="utf-8")


def emit_sans_time(p):
    d = load(p)
    d.pop("GeneratedUtc", None)
    return d


UNLIMITED = {}


def unlimited():
    if not UNLIMITED:
        e, j, side = outputs("unlimited")
        r = lab().probe("with", ["--emit", e, "--json", j])
        UNLIMITED.update(r=r, e=e, j=j, side=side)
    return UNLIMITED


def cr16():
    u = unlimited()
    assert u["r"].returncode == 0, f"unlimited run: exit {u['r'].returncode}: {plain(u['r'])[-800:]}"
    assert "memory route: fast" in u["r"].stdout, f"the unlimited run did not say fast: {u['r'].stdout[-1500:]}"
    e, j, side = outputs("gc-limit")
    r = lab().probe("with", ["--emit", e, "--json", j], env={"DOTNET_GCHeapHardLimit": "0x20000000"})   # 512 MiB
    assert r.returncode == 0, f"limited run: exit {r.returncode}: {plain(r)[-800:]}"
    assert "memory route: lean" in r.stdout and "GC heap hard limit" in r.stdout, f"no lean route naming the limit: {r.stdout[-1500:]}"
    assert side.exists(), "no sidecar beside the emit"
    s = load(side)
    assert s.get("route") == "lean" and s.get("case") == "absent", f"sidecar: {s}"
    assert load(u["side"]).get("route") == "fast", f"unlimited sidecar: {load(u['side'])}"
    assert emit_sans_time(e) == emit_sans_time(u["e"]), "the lean emit differs from the fast one"
    assert Path(j).read_bytes() == Path(u["j"]).read_bytes(), "the lean rows differ from the fast ones"
    assert load(u["e"]).get("Conflicts"), "the fixture's emit has no row: the identity proves nothing"


HELD = "total=64000000000;avail=1000000000;source=none;others=memhold-fixture:4242:26000000000"


def cr17():
    e, j, side = outputs("ask")
    stale(e, j)
    r = lab().probe("with", ["--emit", e, "--json", j, "--memory-route", "ask"], env={"HC_MEMROUTE_TEST_READINGS": HELD})
    no_stack(r)
    assert r.returncode == 5, f"exit {r.returncode}, want 5: {plain(r)[-1200:]}"
    last = r.stderr.strip().splitlines()[-1] if r.stderr.strip() else ""
    assert "memhold-fixture" in last and "--memory-route fast" in last and "--memory-route lean" in last, f"stderr: {r.stderr[-800:]}"
    assert "HC_MEMROUTE_TEST_READINGS" in r.stdout, "the run did not say its readings were the test's"
    assert not e.exists() and not j.exists(), "an earlier run's emit or rows were left at the output paths"
    assert "==== 2. RELATIONSHIPS" not in r.stdout, "the host loop ran"


def cr18():
    u = unlimited()
    e, j, side = outputs("auto-held")
    r = lab().probe("with", ["--emit", e, "--json", j], env={"HC_MEMROUTE_TEST_READINGS": HELD})
    no_stack(r)
    assert r.returncode == 0, f"exit {r.returncode}: {plain(r)[-800:]}"
    assert "memhold-fixture" in r.stdout and "unattended default" in r.stdout and "memory route: lean" in r.stdout, r.stdout[-1500:]
    s = load(side)
    assert s.get("route") == "lean" and s.get("case") == "contended" and s.get("unattendedDefault") is True \
        and "memhold-fixture" in (s.get("warning") or ""), f"sidecar: {s}"
    # CR-11: the lean route's heap limit is really applied at run time (AppContext + GC.RefreshMemoryLimit): the GC reports it.
    assert isinstance(s.get("heapLimit"), int) and s.get("gcLimitAfter") == s.get("heapLimit"), \
        f"CR-11: the heap limit asked {s.get('heapLimit')} is not what the GC reports after the refresh {s.get('gcLimitAfter')}"
    assert emit_sans_time(e) == emit_sans_time(u["e"]) and Path(j).read_bytes() == Path(u["j"]).read_bytes(), "lean output differs"


def cr14():
    e, j, side = outputs("oom")
    stale(e, j)
    r = lab().probe("with", ["--emit", e, "--json", j], env={"HC_CONTACT_FAULT": "oom-host-loop"})
    no_stack(r)
    assert r.returncode == 2, f"exit {r.returncode}, want 2: {plain(r)[-1200:]}"
    last = r.stderr.strip().splitlines()[-1] if r.stderr.strip() else ""
    assert "out of memory" in last and "--memory-route lean" in last, f"stderr: {r.stderr[-800:]}"
    assert not e.exists() and not j.exists(), "an emit or rows file was left that a reader could take for a finished run"
    assert side.exists() and load(side).get("outcome") == "failed: out of memory", f"sidecar: {load(side) if side.exists() else None}"


def cr19():
    e, j, side = outputs("nothing-fits")
    stale(e, j)
    r = lab().probe("with", ["--emit", e, "--json", j],
                    env={"HC_MEMROUTE_TEST_READINGS": "total=64000000000;avail=30000000000;limit=50000000;source=job object"})
    no_stack(r)
    assert r.returncode == 2, f"exit {r.returncode}, want 2: {plain(r)[-1200:]}"
    last = r.stderr.strip().splitlines()[-1] if r.stderr.strip() else ""
    assert "lean" in last and "limit" in last, f"stderr: {r.stderr[-800:]}"
    assert not e.exists() and not j.exists(), "an emit or rows file was left"


def cr20():
    e, j, side = outputs("oom-early")
    stale(e, j)
    r = lab().probe("with", ["--emit", e, "--json", j], env={"HC_CONTACT_FAULT": "oom-before-route"})
    no_stack(r)
    assert r.returncode == 2, f"exit {r.returncode}, want 2: {plain(r)[-1200:]}"
    last = r.stderr.strip().splitlines()[-1] if r.stderr.strip() else ""
    assert "before the memory route was decided" in last and "raise the limit" in last, f"stderr: {r.stderr[-800:]}"
    assert not e.exists() and not j.exists(), "an emit or rows file was left"



def planted_history(records=200_000):
    """A run-log folder whose history.jsonl is about 330 MB of well-formed records (the estimator reads them all at start-up)."""
    d = lab_free_tmp() / "runlog"
    (d / "events").mkdir(parents=True, exist_ok=True)
    pad = "x" * 1300
    with open(d / "history.jsonl", "w", encoding="utf-8", newline="\n") as f:
        for i in range(records):
            f.write('{"schemaVersion":1,"runId":"fixture-history-%07d","attempt":1,"op":"fixture-history","mode":"default",'
                    '"toolVersion":"1.0.0","algoEpoch":1,"compatKey":"000000000000","host":{"machine":"fixture","os":"windows",'
                    '"cores":16,"runtime":"9.0.8"},"resources":{"argc":1,"args":"%s%07d"},"status":"Completed","outcome":"Refused",'
                    '"stageSource":"SingleStage","exitCode":1,"startedUtc":"2026-09-06T22:27:24+00:00",'
                    '"endedUtc":"2026-09-06T22:27:25+00:00","wallSeconds":1.0}\n' % (i, pad, i))
    return d


TMP = []


def lab_free_tmp():
    if not TMP:
        TMP.append(Path(tempfile.mkdtemp(prefix="contact-memory-startup-")))
    return TMP[0]


def cr21():
    runlog = planted_history()
    size = (runlog / "history.jsonl").stat().st_size
    assert size > 300_000_000, f"the planted history is {size:,} bytes: too small to stand for the real one"
    missing = lab_free_tmp() / "no-such-base.json"
    try:
        start_up_under_limit(runlog, missing)
    finally:
        (runlog / "history.jsonl").unlink()


def start_up_under_limit(runlog, missing):
    for gc in ("0", "1"):
        r = run([exe(tcr.PROBES, "contactprobe"), "--base", missing, "--emit", lab_free_tmp() / "emit.json"],
                env=dict(os.environ, DOTNET_GCHeapHardLimit="0x20000000", DOTNET_gcServer=gc, HOUSECARL_RUNLOG_DIR=str(runlog)))
        assert "Out of memory." not in plain(r), f"gcServer={gc}: a bare out-of-memory at start-up: exit {r.returncode}: {plain(r)[-600:]}"
        no_stack(r)
        last = r.stderr.strip().splitlines()[-1] if r.stderr.strip() else ""
        assert r.returncode == 2 and last == f"--base input does not exist: {missing}", \
            f"gcServer={gc}: exit {r.returncode}, want 2 and the missing-input sentence: {plain(r)[-600:]}"


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    for name, fn in [("CR-16 a heap limit below the fast need: lean, named, same rows", cr16),
                     ("CR-17 contention with ask: exit 5, one sentence, stale outputs gone", cr17),
                     ("CR-18 contention under auto: warned, lean, unattended default recorded", cr18),
                     ("CR-14 out of memory in the host loop: one sentence, no partial or stale output", cr14),
                     ("CR-19 nothing fits: one sentence, no output", cr19),
                     ("CR-20 out of memory before the route is decided: one sentence, exit 2, no output", cr20),
                     ("CR-21 start-up garbage under a heap limit: the input sentence, never a bare out-of-memory", cr21)]:
        if len(sys.argv) > 1 and not any(name.startswith(a + " ") for a in sys.argv[1:]):
            continue
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    sys.exit(1 if FAILURES else 0)
