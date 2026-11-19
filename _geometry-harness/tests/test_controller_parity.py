"""Known answers for controller_parity.py, the tool that compares two run controllers' run folders.

    python _geometry-harness/tests/test_controller_parity.py

Each case builds two small run folders by hand in a temporary folder. The two hold the same evaluation but differ in
everything a rerun changes: the run folder's path, the checkout path, the plan id, receipt ids, run ids, times and
durations. P1 the two compare equal; P2 each planted kind of difference is caught and named; P3 a ledger entry that
points at another receipt's id is caught; P4 a field the tool has never heard of is compared; P5 a probe build hash
is compared between runs of one checkout and not between two checkouts; P6 only a list the table names is compared as
a bag; P7 a time that exists on one side only is caught; P8 a whole number against a decimal is caught; P9 a recorded
hash that names the wrong run file is caught; P10 a file only one side wrote is caught; P11 the exclusion table is well
formed and printed in full; P12 the command's exit codes; P13 each measured time or byte figure that sits inside a
compared line, sentence or field is set aside, and a change to the rest of that line, sentence or field is still caught;
P14 the machine slot command's own lines in a heavy step's log are left out and every other line is compared; P15 the
controller program is set aside after the slot command's `--` and the slot command, the step and its arguments are
compared; P16 the controller's own entry in an enrichment's tool list is set aside and every other entry is compared;
P17 contactprobe's readings of this machine's memory are set aside, in the file beside an emit and in its log, and the
route, the settings and the other words are compared; P18 a hash is named the same way on both sides when two files
hold the same bytes on one side only, and a hash that names another file is still caught; P19 the seconds in the second
line of a stopped child's log are set aside and the rest of the stop sentence is compared; P20 the cache key of a job
stopped before it was stored is named after the job on its cache log line, and another job on that line is caught.
No probe, no controller and no game is started.
"""
from __future__ import annotations

import contextlib
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.insert(0, str(HARNESS))
import controller_parity as CP  # noqa: E402

FAILURES, PASSED = [], []
WORLD = "C:/games/world/instance"
KEYS = ("check.one__baseline", "check.one__candidate-01", "check.two__candidate-01")


def check(ok, label):
    (PASSED if ok else FAILURES).append(label)
    print(("GREEN " if ok else "RED   ") + label)


def guarded(label, fn):
    try:
        fn()
    except Exception:  # a crash is a failed case, named
        FAILURES.append(label + " (crashed)")
        print(f"CRASH {label}\n{traceback.format_exc()}")


def sha(data) -> str:
    return hashlib.sha256(data if isinstance(data, bytes) else data.encode("utf-8")).hexdigest()


def put(path: Path, doc) -> str:
    """Writes one file (JSON for a dict or list, text otherwise) and returns its sha256."""
    path.parent.mkdir(parents=True, exist_ok=True)
    data = (doc if isinstance(doc, str) else json.dumps(doc, indent=1)).encode("utf-8")
    path.write_bytes(data)
    return sha(data)


def build(root: Path, n: int, *, repo=None, findings=0):
    """One hand-written run folder under root/run; n changes every id, time, duration and path. Returns the run folder."""
    run, ev = root / "run", root / "run" / "evaluation"
    repo = repo or (root / f"checkout-{n}")
    plan_id = sha(f"plan{n}")[:12]
    run_id = f"evalrun-checks-2026111{n}T10000000{n}-{sha(str(n))[:8]}"
    rid = {k: sha(f"{k}{n}")[:16] for k in KEYS}
    t = f"2026-11-1{n}T10:00:0{n}Z"

    put(run / "arms.json", [
        {"id": "combined", "role": "the profile as it is", "keys": [], "evaluated": ["Sel.esp"],
         "plugins": ["World.esm", "Sel.esp"], "mods": ["SelMod", "WorldMod"], "switchedOn": [], "appendedPlugins": []},
        {"id": "without-selection", "role": "the profile without the selected mods", "keys": [], "evaluated": [],
         "plugins": ["World.esm"], "mods": ["WorldMod"]}])
    put(run / "status.json", {"status": "incomplete", "through": "bundle", "sentence": "scenes are not built.",
                              "refusals": [{"stage": "scenes", "sentence": "scenes are not built."}],
                              "at": f"2026-11-1{n}T10:00:09+00:00"})
    put(run / "plan/commands/plan.txt",
        f"$ python {repo}\\_geometry-harness\\evalrun.py plan --out {ev}\n(exit 0, {n}.{n}s)\n\nplanned 2 checks\n")

    out_sha = put(ev / "outputs/check.one/baseline.json",
                  {"Probe": "oneprobe", "GeneratedUtc": t, "Conflicts": [{"key": "000810:World.esm", "depth": 12}],
                   "Configuration": {"Plugins": [{"Name": "World.esm", "Path": WORLD + "/mods/WorldMod/World.esm"}]}})
    plan = {
        "schemaVersion": 2, "planId": plan_id, "createdUtc": t,
        "tools": {"oneprobe": {"exe": f"{repo}\\_geometry-harness\\oneprobe\\oneprobe.exe", "exeSha256": sha("oneprobe build"),
                               "present": True}},
        "toolIdentity": {"contract": 1, "programs": {"controller": {
            "kind": "scripts", "state": "present", "digest": "scripts:" + sha(f"controller{n}"),
            "locator": f"{repo}\\_geometry-harness", "files": [{"name": "evalrun.py", "sha256": sha(f"evalrun{n}")}]}}},
        "runner": {"evalrun.py": sha(f"evalrun{n}")},
        "candidatesDir": str(run / "prepare" / "candidates"),
        "arms": [{"armId": a, "candidateFile": a + ".json", "candidatePath": str(run / "prepare" / "candidates" / (a + ".json")),
                  "requested": {"plugins": [{"name": "World.esm", "sha256": sha("World.esm"),
                                             "path": WORLD + "/mods/WorldMod/World.esm"}],
                                "providers": [{"name": "WorldMod", "kind": "mod"}]},
                  "scenePreflight": {"status": "passed"}} for a in ("baseline", "candidate-01")],
        "states": {"selected": ["baseline"], "depth": "standard", "notExplored": []},
        "checks": [{"checkId": c, "required": True, "applicability": "yes", "adapter": {"kind": "probe", "exe": "oneprobe"},
                    "arms": [{"armId": "baseline", "status": "planned"}, {"armId": "candidate-01", "status": "planned"}],
                    "status": "planned"} for c in ("check.one", "check.two")],
        "budgets": {"minutes": 90.0, "maxCandidates": 2},
        "explicitDecisions": {"select": ["check.one", "check.two"], "historicalClassifications": ["b", "a", "c"]},
    }
    plan_sha = put(ev / "plan.json", plan)
    put(ev / "plan.sha256", plan_sha + "\n")

    receipt_sha = {}
    for k in KEYS:
        check_id, arm = k.split("__")
        outputs = [{"path": "outputs/check.one/baseline.json", "sha256": out_sha, "bytes": 100 + n,
                    "mtimeUtc": t}] if k == KEYS[0] else []
        receipt_sha[k] = put(ev / "receipts" / (k + ".json"), {
            "schemaVersion": 2, "receiptId": rid[k], "planId": plan_id, "checkId": check_id, "armId": arm, "part": None,
            "adapter": {"kind": "probe", "exe": "oneprobe"}, "argvShape": ["<abs>/oneprobe.exe", "--emit", "<RUN>/outputs"],
            "runId": run_id, "parentRunId": None, "startedUtc": t, "endedUtc": t, "exitCode": 0, "outputs": outputs,
            "consumed": {"configuration": {"fingerprint": sha("fingerprint"), "population": "load-time", "state": None,
                                           "plugins": [{"name": "World.esm", "sha256": sha("World.esm")}],
                                           "providers": [{"name": "WorldMod", "kind": "mod"}]},
                         "optionsSha256": sha(f"options{n}"),
                         "programs": {"controller": {"program": "controller", "kind": "scripts",
                                                     "digest": "scripts:" + sha(f"controller{n}"), "checked": ["launch"]}}},
            "validation": {"accepted": True, "rejections": []},
            "result": {"execution": "completed", "detector": "no-finding", "findings": findings, "coverage": [],
                       "verdict": None}})
    ledger = {"schemaVersion": 2, "planId": plan_id,
              "runIds": [{"runId": run_id, "parentRunId": None, "command": "run", "startedUtc": t}],
              "entries": {k: {"checkId": k.split("__")[0], "armId": k.split("__")[1], "part": None, "status": "completed",
                              "cause": None, "receiptId": rid[k], "detector": "no-finding",
                              "validation": {"accepted": True, "rejections": []},
                              "receiptPath": f"receipts/{k}.json", "runId": run_id, "updatedUtc": t} for k in KEYS}}
    ledger_sha = put(ev / "ledger.json", ledger)
    put(ev / "journal.jsonl", "".join(json.dumps(
        {"armId": k.split("__")[1], "checkId": k.split("__")[0], "from": "running", "to": "completed",
         "receiptId": rid[k], "runId": run_id, "t": t}, sort_keys=True) + "\n" for k in KEYS))
    disposition = {"schemaVersion": 2, "planId": plan_id, "createdUtc": t, "runIds": [run_id],
                   "derivedFrom": {"ledgerSha256": ledger_sha, "receipts": sha("".join(receipt_sha.values())), "journalLines": 3},
                   "evaluation": {"complete": False, "claim": "incomplete", "missing": [{"checkId": "check.two", "armId": "baseline"}]},
                   "ledgerCounts": {"completed": 3}}
    disp_sha = put(ev / "disposition.json", disposition)
    put(ev / "STATUS.md", f"# Evaluation {plan_id}\n\nincomplete: check.two on baseline has no result\n")

    # the bundle: copies, a manifest with each copy's hash, and the verification
    b = run / "bundle" / "evidence"
    copies = {"plan.json": ev / "plan.json", "ledger.json": ev / "ledger.json", "disposition.json": ev / "disposition.json",
              "evidence/outputs/check.one/baseline.json": ev / "outputs/check.one/baseline.json"}
    copies.update({f"receipts/{k}.json": ev / "receipts" / (k + ".json") for k in KEYS})
    evidence = []
    for rel, src in copies.items():
        (b / rel).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, b / rel)
        evidence.append({"id": sha(rel)[:16], "kind": "evidence" if rel.startswith("evidence/") else "control", "path": rel,
                         "sha256": sha((b / rel).read_bytes()), "bytes": (b / rel).stat().st_size,
                         "checkId": "check.one" if rel.startswith("evidence/") else None,
                         "armId": "baseline" if rel.startswith("evidence/") else None,
                         "strength": "record-fact" if rel.startswith("evidence/") else None})
    put(b / "manifest.json", {"bundleSchema": 2, "planId": plan_id, "createdUtc": t, "runIds": [run_id],
                              "required": list(copies), "evidence": evidence,
                              "enrichmentStatus": {"names": {"status": "enriched", "seconds": float(n)}},
                              "order": {"status": "unknown", "pair": ["Sel.esp"]}})
    put(b / "verify.json", {"schemaVersion": 1, "checkedUtc": t, "dir": str(b), "planId": plan_id,
                            "bundle": {"complete": True, "verified": True, "missing": [], "mismatched": [], "defects": []},
                            "evaluation": {"complete": False, "claim": "incomplete"},
                            "toolIdentity": {"guarantee": "bound", "defects": []},
                            "receiptOutputs": [{"receipt": KEYS[0], "receiptId": rid[KEYS[0]], "sha256": out_sha}]})
    put(run / "stages.json", {"schema": 1, "stages": {
        "plan": {"stage": "plan", "at": f"2026-11-1{n}T10:00:05+00:00", "exitCode": 0, "accepted": True,
                 "inputsSha256": sha("plan inputs"), "inputs": {"entry": sha("entry"), "upstream": {}},
                 "outputs": {(ev / "plan.json").as_posix(): plan_sha}, "bound": {"plan": plan_sha},
                 "seconds": float(n), "driverSha256": sha(f"driver{n}"), "disk": {"addedBytes": 1000 + n, "evaluatedRecords": 7}},
        "bundle": {"stage": "bundle", "at": f"2026-11-1{n}T10:00:08+00:00", "exitCode": 0, "accepted": True,
                   "inputsSha256": sha("bundle inputs"), "inputs": {"entry": sha("entry"), "upstream": {"plan": sha("plan inputs")}},
                   "outputs": {(ev / "disposition.json").as_posix(): disp_sha}, "bound": {},
                   "seconds": float(n) / 2, "driverSha256": sha(f"driver{n}"), "disk": {"addedBytes": 2000 + n, "evaluatedRecords": 7}}},
        "kept": {}, "history": []})
    put(root / "stdout.txt", f"preparation: not reused\nplan: accepted in {n}.{n} s\nscope: Sel edits 1 interior\n"
                             f"bundle: accepted in 0.{n} s\n")
    put(root / "stderr.txt", "")
    put(root / "exit-code.txt", "0")
    return run


class Pair:
    """Two freshly built runs, A and B, that hold the same evaluation."""

    def __init__(self, tmp: Path, name: str, *, same_checkout=False):
        self.root = tmp / name
        shared = self.root / "checkout" if same_checkout else None
        self.a = build(self.root / "side-a", 1, repo=shared)
        self.b = build(self.root / "a-longer-side-b", 2, repo=shared)

    def compare(self) -> dict:
        return CP.compare(CP.Side("A", self.a), CP.Side("B", self.b))

    def lines(self) -> list[str]:
        return [d["line"] for s in self.compare()["sections"] for d in s["differences"]]


def edit(path: Path, change):
    doc = json.loads(path.read_text(encoding="utf-8"))
    change(doc)
    path.write_text(json.dumps(doc, indent=1), encoding="utf-8")


def names(lines, file, where="") -> bool:
    return any(f"] {file} :: " in line and (not where or f":: {where} ::" in line) for line in lines)


def p1(tmp):
    report = Pair(tmp, "p1").compare()
    check(report["equal"], "P1: two runs that differ only in paths, ids, times and tool builds compare equal "
          f"({[d['line'] for s in report['sections'] for d in s['differences']][:3]})")
    matched = {r["id"]: r["matched"] for r in report["exclusions"]}
    check(all(matched[k] > 0 for k in ("path-run", "path-repo", "id-plan", "id-receipt", "id-run", "hash-of-run-file",
                                       "time-utc-fields", "seconds-fields", "controller-identity")),
          f"P1: the report counts what each exclusion row matched ({ {k: v for k, v in matched.items() if v} })")
    by_name = {s["name"]: s for s in report["sections"]}
    check(all(by_name[n]["values"] > 0 for n in ("status", "arms", "stage decisions", "plan", "ledger", "receipts",
                                                 "disposition", "bundle", "probe outputs", "file inventory")),
          "P1: every part of the report compared something")
    check(report["differentCheckout"] and not report["notChecked"], "P1: the two checkouts are told apart; nothing went unchecked")


def p2(tmp):
    for kind in CP.PLANT_KINDS:
        pair = Pair(tmp, "p2-" + kind)
        what = CP.plant(kind, pair.b)
        lines = pair.lines()
        check(bool(lines) and names(lines, what["file"], what["where"]),
              f"P2: planted {kind} is caught and named at {what['file']} {what['where']} ({len(lines)} line(s))")


def p3(tmp):
    pair = Pair(tmp, "p3")
    def wrong(doc):
        doc["entries"][KEYS[0]]["receiptId"] = doc["entries"][KEYS[1]]["receiptId"]
    edit(pair.b / "evaluation/ledger.json", wrong)
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, "evaluation/ledger.json", f"entries/{KEYS[0]}/receiptId")
          and f"<RECEIPT:{KEYS[1]}>" in lines[0], f"P3: a ledger entry pointing at another receipt's id is caught ({lines})")


def p4(tmp):
    pair = Pair(tmp, "p4-one-side")
    edit(pair.b / "evaluation/plan.json", lambda d: d.update(somethingNew={"value": 1}))
    lines = pair.lines()
    check(names(lines, "evaluation/plan.json", "somethingNew"), f"P4: a new field only one side writes is caught ({lines})")
    pair = Pair(tmp, "p4-both-sides")
    edit(pair.a / "evaluation/receipts" / (KEYS[0] + ".json"), lambda d: d["result"].update(somethingNew="x"))
    edit(pair.b / "evaluation/receipts" / (KEYS[0] + ".json"), lambda d: d["result"].update(somethingNew="y"))
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, f"evaluation/receipts/{KEYS[0]}.json", "result/somethingNew"),
          f"P4: a new field with a different value on each side is caught; nothing is skipped by default ({lines})")


def p5(tmp):
    def other_build(doc):
        doc["tools"]["oneprobe"]["exeSha256"] = sha("another build")
    pair = Pair(tmp, "p5-two-checkouts")
    edit(pair.b / "evaluation/plan.json", other_build)
    check(pair.compare()["equal"], "P5: a probe build hash is not compared between two checkouts")
    pair = Pair(tmp, "p5-one-checkout", same_checkout=True)
    check(pair.compare()["equal"], "P5: two runs of one checkout compare equal")
    edit(pair.b / "evaluation/plan.json", other_build)
    lines = pair.lines()
    check(names(lines, "evaluation/plan.json", "tools/oneprobe/exeSha256"),
          f"P5: a probe build hash that differs between two runs of one checkout is caught ({lines})")


def p6(tmp):
    pair = Pair(tmp, "p6-bag")
    edit(pair.b / "evaluation/plan.json", lambda d: d["explicitDecisions"].update(historicalClassifications=["c", "a", "b"]))
    check(pair.compare()["equal"], "P6: the list the table names as unordered is compared as a bag")
    edit(pair.b / "evaluation/plan.json", lambda d: d["explicitDecisions"].update(historicalClassifications=["c", "a", "x"]))
    check(names(pair.lines(), "evaluation/plan.json"), "P6: a different member of that bag is still caught")
    pair = Pair(tmp, "p6-ordered")
    edit(pair.b / "evaluation/plan.json", lambda d: d["explicitDecisions"].update(select=["check.two", "check.one"]))
    lines = pair.lines()
    check(names(lines, "evaluation/plan.json", "explicitDecisions/select[0]"), f"P6: any other list keeps its order ({lines})")
    pair = Pair(tmp, "p6-named")
    edit(pair.b / "evaluation/plan.json", lambda d: d["checks"].reverse())
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, "evaluation/plan.json", "checks (order)"),
          f"P6: a list read by name still has its order compared ({lines})")


def p7(tmp):
    pair = Pair(tmp, "p7")
    edit(pair.b / "evaluation/disposition.json", lambda d: d.pop("createdUtc"))
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, "evaluation/disposition.json", "createdUtc"),
          f"P7: a time only one side wrote is caught, though its value is never compared ({lines})")


def p8(tmp):
    pair = Pair(tmp, "p8")
    edit(pair.b / "evaluation/plan.json", lambda d: d["budgets"].update(minutes=90))
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, "evaluation/plan.json", "budgets/minutes"), f"P8: 90 against 90.0 is caught ({lines})")


def p9(tmp):
    pair = Pair(tmp, "p9")
    wrong = sha((pair.b / "evaluation/ledger.json").read_bytes())
    edit(pair.b / "evaluation/receipts" / (KEYS[0] + ".json"), lambda d: d["outputs"][0].update(sha256=wrong))
    lines = pair.lines()
    check(names(lines, f"evaluation/receipts/{KEYS[0]}.json", "outputs[0]/sha256") and "ledger.json>" in "".join(lines),
          f"P9: a recorded hash that is the hash of the wrong run file is caught ({lines[:2]})")


def p10(tmp):
    pair = Pair(tmp, "p10")
    put(pair.b / "evaluation/outputs/check.two/extra.json", {"rows": []})
    put(pair.a / "runlog/events/x.jsonl", "{}\n")
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, "evaluation/outputs/check.two/extra.json") and "[file inventory]" in lines[0],
          f"P10: a file only one side wrote is caught; the run log folder is not part of the inventory ({lines})")
    pair = Pair(tmp, "p10-stdout")
    put(pair.b.parent / "stdout.txt", "preparation: not reused\nplan: REFUSED: no such check\n")
    put(pair.b.parent / "exit-code.txt", "2")
    lines = pair.lines()
    check(names(lines, "<exit-code>") and names(lines, "<stdout>", "line 2"),
          f"P10: a different exit code and a different stage line on stdout are caught ({lines})")


def p11(_tmp):
    rows = CP.EXCLUSIONS
    actions = {"ignore", "present-only", "normalise-path", "normalise-id", "unordered", "drop-line"}
    check(all(r.get("id") and r.get("files") and r.get("where") and r.get("action") in actions
              and len(r.get("reason", "")) > 10 for r in rows), "P11: every exclusion row has an id, files, a place, a known action and a reason")
    check(len({r["id"] for r in rows}) == len(rows), "P11: the row ids are unique")
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        rc = CP.main(["exclusions"])
    text = out.getvalue()
    check(rc == 0 and all(r["id"] in text and r["reason"] in text for r in rows), "P11: `exclusions` prints every row with its reason")


def p12(tmp):
    pair = Pair(tmp, "p12")
    env = {**os.environ, "HOUSECARL_RUNLOG_DIR": str(tmp / "runlog"), "PYTHONIOENCODING": "utf-8"}
    tool = [sys.executable, str(HARNESS / "controller_parity.py")]
    def run(*args):
        return subprocess.run(tool + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8", env=env)
    r = run("compare", pair.a, pair.b, "--report", tmp / "p12.json")
    report = json.loads((tmp / "p12.json").read_text(encoding="utf-8")) if (tmp / "p12.json").is_file() else {}
    check(r.returncode == 0 and "EQUAL after exclusions" in r.stdout and report.get("equal") is True,
          f"P12: equal runs exit 0 and write the report ({r.returncode} {r.stderr[-200:]})")
    planted = run("plant", "ledger-status", pair.b)
    r = run("compare", pair.a, pair.b)
    check(planted.returncode == 0 and r.returncode == 1 and "DIFF [ledger] evaluation/ledger.json" in r.stdout,
          f"P12: different runs exit 1 with one line per difference ({r.returncode})")
    r = run("compare", pair.a, tmp / "no-such-run")
    check(r.returncode == 2 and r.stderr.startswith("REFUSED: "), f"P12: a missing run folder is refused with exit 2 ({r.returncode})")
    r = run("plant", "no-such-kind", pair.b)
    check(r.returncode == 2, f"P12: an unknown planted kind is a usage error, exit 2 ({r.returncode})")


def append(path: Path, text: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "a", encoding="utf-8", newline="\n") as fh:
        fh.write(text)


STOP = ("this run's budget of 0.03 minute(s) was reached while check check.two [baseline] was running: its process tree "
        "was stopped at the deadline after {t}s (confirmed gone); nothing it wrote is accepted")
BUDGET = "this run's budget of 0.03 minute(s) was reached {t} minutes into this run, before this check launched"
FORECAST = ("disk forecast for evaluate..scenes: about 24{n}.8 KiB more on C: (7 evaluated records at 3{n}.3 KiB each, "
            "measured at: {run} evaluate (0.0{n} GiB over 7 records)); no figure for scenes; C: has 61{n}.0 GiB free and "
            "keeps a 22 GiB floor.")

# row id, the file, a line or value holding a measured figure ({n} is 1 on side A and 2 on side B), and the same line
# with something else changed, which must still be caught
TIMED_LINES = [
    ("seconds-probe-log-ms", "evaluation/logs/check.one__baseline.txt", "load order: 2 plugins (2{n} ms)", "load order: 3 plugins (21 ms)"),
    ("seconds-probe-log-paren", "evaluation/logs/check.one__baseline.txt", "indexed 1 placements over 1 refs  (0.{n}s)",
     "indexed 2 placements over 1 refs  (0.1s)"),
    ("seconds-probe-log-paren", "evaluation/logs/check.one__baseline.txt", "configuration: 1 plugins, 0 archives (hashed in 0.{n}s)",
     "configuration: 1 plugins, 1 archives (hashed in 0.1s)"),
    ("seconds-probe-log-paren", "evaluation/logs/check.one__baseline.txt", "   validation memo: 0 hits after eviction (0.{n}s, 0 meshes prepared again)",
     "   validation memo: 0 hits after eviction (0.1s, 1 meshes prepared again)"),
    ("seconds-probe-log-elapsed", "evaluation/logs/check.one__baseline.txt", "elapsed 2.{n}s", "elapsed 2.1s more"),
    ("seconds-probe-log-elapsed", "evaluation/sweep/inv-0001/logs/oneprobe-baseline.txt", "elapsed           : 0.{n}s", "elapsed total     : 0.1s"),
    ("seconds-probe-log-in", "evaluation/logs/check.one__baseline.txt", "pair cache: read 0 result(s) from 2 file(s) in 0.{n}s",
     "pair cache: read 1 result(s) from 2 file(s) in 0.1s"),
    ("seconds-probe-log-timers", "evaluation/logs/check.one__baseline.txt",
     "   pair-loop timers same: 0 pairs (0 mesh queries), 0.{n}s; pairframe 0.{n}s, surface 0.{n}s (0 u), unaccounted 0.{n}s; time-outs 0",
     "   pair-loop timers same: 1 pairs (0 mesh queries), 0.1s; pairframe 0.1s, surface 0.1s (0 u), unaccounted 0.1s; time-outs 0"),
    ("seconds-probe-log-timers", "evaluation/logs/check.one__baseline.txt", "   pair-loop stage: 0.{n}s for 0 pairs; same + cross pair wall 0.{n}s; BVH nodes 0",
     "   pair-loop stage: 0.1s for 0 pairs; same + cross pair wall 0.1s; BVH nodes 4"),
    ("seconds-runner-stop-echo", "evaluate/commands/evaluate-01.txt", "   check.two__baseline                          STOPPED at deadline     0.{n}s",
     "   check.one__baseline                          STOPPED at deadline     0.1s"),
    ("bytes-disk-forecast", "<stdout>", FORECAST, FORECAST.replace("7 evaluated records", "8 evaluated records")),
    ("bytes-disk-forecast", "<stdout>", FORECAST, FORECAST.replace("a 22 GiB floor", "a 23 GiB floor")),
]


def timed_pair(tmp, name, change):
    """A pair whose sides each get `change(run folder, n)` applied, n being 1 for A and 2 for B."""
    pair = Pair(tmp, name)
    change(pair.a, 1)
    change(pair.b, 2)
    return pair


def p13(tmp):
    for i, (row, rel, line, other) in enumerate(TIMED_LINES):
        def write(run, n, text=None):
            target = run.parent / "stdout.txt" if rel == "<stdout>" else run / rel
            append(target, (text or line).format(n=n, run=run.as_posix()) + "\n")
        pair = timed_pair(tmp, f"p13-{i}", write)
        report = pair.compare()
        matched = {r["id"]: r["matched"] for r in report["exclusions"]}
        check(report["equal"] and matched[row] >= 2, f"P13: {row}: {line.format(n='N', run='RUN')[:70]!r} compares equal across a different figure "
              f"({[d['line'] for s in report['sections'] for d in s['differences']]})")
        pair = Pair(tmp, f"p13-{i}-rest")
        write(pair.a, 1)
        write(pair.b, 1, other)
        lines = pair.lines()
        check(len(lines) == 1 and names(lines, rel), f"P13: {row}: a change to the rest of that line is caught ({lines})")

    # a sentence inside a JSON field: the ledger entry's detail, the journal line's detail, the receipt's note and rejection
    for row, sentence, other in (("seconds-stopped-sentence", STOP, STOP.replace("(confirmed gone)", "(NOT confirmed gone)")),
                                 ("minutes-budget-sentence", BUDGET, BUDGET.replace("0.03 minute(s)", "0.04 minute(s)"))):
        def write(run, n, text=None):
            said = (text or sentence).format(t=f"0.{n}")
            edit(run / "evaluation/ledger.json", lambda d: d["entries"][KEYS[2]].update(detail=said))
            edit(run / f"evaluation/receipts/{KEYS[2]}.json", lambda d: (d["result"].update(note=said),
                 d["validation"].update(rejections=[{"cause": "budget-exhausted", "detail": said}])))
        pair = timed_pair(tmp, f"p13-{row}", write)
        report = pair.compare()
        matched = {r["id"]: r["matched"] for r in report["exclusions"]}
        check(report["equal"] and matched[row] >= 6, f"P13: {row}: the sentence compares equal across a different figure "
              f"({[d['line'] for s in report['sections'] for d in s['differences']]})")
        pair = Pair(tmp, f"p13-{row}-rest")
        write(pair.a, 1)
        write(pair.b, 1, other)
        lines = pair.lines()
        check(len(lines) == 3 and names(lines, "evaluation/ledger.json", f"entries/{KEYS[2]}/detail")
              and names(lines, f"evaluation/receipts/{KEYS[2]}.json", "result/note"), f"P13: {row}: another word of the sentence is caught ({lines})")
        # the same sentence in a file the row does not name is compared whole
        pair = timed_pair(tmp, f"p13-{row}-elsewhere", lambda run, n: edit(run / "evaluation/disposition.json",
                                                                         lambda d: d.update(said=sentence.format(t=f"0.{n}"))))
        check(names(pair.lines(), "evaluation/disposition.json", "said"), f"P13: {row}: the row reaches only the ledger, the journal and the receipts")

    # the forecast in the two files that keep it
    def forecast(run, n, text=None):
        said = (text or FORECAST).format(n=n, run=run.as_posix())
        edit(run / "status.json", lambda d: d.update(diskForecast=said))
        put(run / "disk-forecast.json", {"line": said, "refused": None, "stages": ["evaluate", "scenes"]})
    pair = timed_pair(tmp, "p13-forecast", forecast)
    check(pair.compare()["equal"], f"P13: bytes-disk-forecast: status.json and disk-forecast.json compare equal across different byte figures ({pair.lines()})")
    pair = Pair(tmp, "p13-forecast-rest")
    forecast(pair.a, 1)
    forecast(pair.b, 1, FORECAST.replace("no figure for scenes", "no figure for records, scenes"))
    lines = pair.lines()
    check(len(lines) == 2 and names(lines, "status.json", "diskForecast") and names(lines, "disk-forecast.json", "line"),
          f"P13: bytes-disk-forecast: another word of the sentence is caught in both files ({lines})")
    pair = timed_pair(tmp, "p13-forecast-refused", lambda run, n: put(run / "disk-forecast.json", {
        "line": None, "refused": f"the run needs about 24{n}.8 KiB more on C: for evaluate", "stages": ["evaluate"]}))
    check(names(pair.lines(), "disk-forecast.json", "refused"), "P13: bytes-disk-forecast: a refusal's figures are compared (the row names the forecast sentence only)")

    # a probe output's own time of writing
    def generated(run, n, edges=0, stamp=True):
        doc = {"schemaVersion": 1, "edges": [1] * edges}
        if stamp:
            doc["generated"] = f"2026-11-19 02:50:4{n}Z"
        put(run / "evaluation/outputs/check.two/baseline.json", doc)
    pair = timed_pair(tmp, "p13-generated", generated)
    report = pair.compare()
    check(report["equal"] and {r["id"]: r["matched"] for r in report["exclusions"]}["probe-generated"] == 2,
          f"P13: probe-generated: a probe output's own time of writing is not compared ({pair.lines()})")
    pair = Pair(tmp, "p13-generated-rest")
    generated(pair.a, 1)
    generated(pair.b, 1, edges=1)
    check(names(pair.lines(), "evaluation/outputs/check.two/baseline.json", "edges[0]"), "P13: probe-generated: the rest of that output is compared")
    pair = Pair(tmp, "p13-generated-missing")
    generated(pair.a, 1)
    generated(pair.b, 1, stamp=False)
    check(names(pair.lines(), "evaluation/outputs/check.two/baseline.json", "generated"), "P13: probe-generated: a time only one side wrote is caught")


SLOT_LINES = ('{{"step": "slot_taken", "slot": "C:\\\\slots\\\\HEAVY-SLOT", "pid": {pid}, "t": "2026-11-18 20:01:2{n} Pacific Standard Time"}}\n'
              "{waits}"
              "idle on 1 checks at 2026-11-18 20:01:3{n} Pacific Standard Time (free 2195{n} MB)\n"
              '{{"step": "taken", "pid": {pid}, "maxMinutes": 100.0, "disk": [{{"volume": "C:", "freeGiB": 616.0{n}}}]}}\n'
              "   check.one__baseline          exit 0      0.{n}s\n"
              '{{"step": "released", "pid": {pid}, "heldMinutes": 0.0{n}}}\n')
WAIT = "waiting for idle (up to 5 min, 1 consecutive checks) from 2026-11-18 20:01:2{n} Pacific Standard Time\n"


def p14(tmp):
    def slot(run, n, extra=""):
        append(run / "evaluate/commands/evaluate-01.txt",
               SLOT_LINES.format(pid=1000 * n, n=n, waits=WAIT.format(n=n) * n) + extra)   # side B waited twice
    pair = timed_pair(tmp, "p14", slot)
    report = pair.compare()
    check(report["equal"] and {r["id"]: r["matched"] for r in report["exclusions"]}["slot-command-lines"] == 11,
          f"P14: the slot command's own lines in a heavy step's log are left out, however many each side has ({pair.lines()})")
    pair = Pair(tmp, "p14-rest")
    slot(pair.a, 1)
    slot(pair.b, 1, "   check.two__baseline          exit 3      0.1s\n")
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, "evaluate/commands/evaluate-01.txt"), f"P14: a line of the step itself that only one side has is caught ({lines})")
    pair = Pair(tmp, "p14-elsewhere")
    append(pair.a / "evaluation/logs/check.one__baseline.txt", '{"step": "taken", "pid": 1}\n')
    append(pair.b / "evaluation/logs/check.one__baseline.txt", '{"step": "taken", "pid": 2}\n')
    check(names(pair.lines(), "evaluation/logs/check.one__baseline.txt"), "P14: the same line in a probe's log is compared (the row names a stage's command logs only)")
    pair = Pair(tmp, "p14-other-line")
    append(pair.a / "evaluate/commands/evaluate-01.txt", "idle check refused: memory low at 2026-11-18 20:01:21\n")
    append(pair.b / "evaluate/commands/evaluate-01.txt", "idle check refused: memory low at 2026-11-18 20:01:22\n")
    check(names(pair.lines(), "evaluate/commands/evaluate-01.txt"), "P14: a line of another shape that mentions idle is compared")


def p15(tmp):
    slot = "$ python S:/tools/idle_lock.py hold --lock L:/locks/HEAVY-LOCK-x --max-min {hold} -- "

    def first_line(run, program, sub="run", hold=100):
        put(run / "evaluate/commands/evaluate-01.txt", slot.format(hold=hold) + f"{program} {sub} --run R --budget-minutes 5\n(exit 0, 1.0s)\n\n")
    pair = Pair(tmp, "p15")
    first_line(pair.a, f"python {pair.root}\\side-a\\checkout-1\\_geometry-harness\\evalrun.py")
    first_line(pair.b, f"{pair.root}\\a-longer-side-b\\checkout-2\\_geometry-harness\\runcontrol\\bin\\runcontrol.exe")
    report = pair.compare()
    matched = {r["id"]: r["matched"] for r in report["exclusions"]}
    check(report["equal"] and matched["controller-command-python"] >= 2 and matched["controller-command-exe"] >= 1,
          f"P15: the controller program after the slot command's `--` is set aside ({pair.lines()})")
    first_line(pair.b, f"{pair.root}\\a-longer-side-b\\checkout-2\\_geometry-harness\\runcontrol\\bin\\runcontrol.exe", sub="resume")
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, "evaluate/commands/evaluate-01.txt", "line 1"), f"P15: another subcommand after the `--` is caught ({lines})")
    first_line(pair.b, f"{pair.root}\\a-longer-side-b\\checkout-2\\_geometry-harness\\runcontrol\\bin\\runcontrol.exe", hold=30)
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, "evaluate/commands/evaluate-01.txt", "line 1"), f"P15: another slot command before the `--` is caught ({lines})")
    first_line(pair.b, "L:/other/tool.exe")
    check(names(pair.lines(), "evaluate/commands/evaluate-01.txt", "line 1"), "P15: a program that is not a controller is compared by name")


def matched(report, row):
    return {r["id"]: r["matched"] for r in report["exclusions"]}[row]


def p16(tmp):
    rel = "bundle/evidence/enrichment/names/enrichment.json"

    def enrichment(run, controller, reader="reader build", with_hash=True):
        own = {"file": controller}
        if with_hash:
            own["sha256"] = sha(controller)
        put(run / rel, {"schemaVersion": 1, "kind": "names", "tool": [own, {"file": "housecarl-mcp.exe", "sha256": sha(reader)}]})
    pair = Pair(tmp, "p16")
    enrichment(pair.a, "bundleenrich.py")
    enrichment(pair.b, "runcontrol.dll")
    report = pair.compare()
    check(report["equal"] and matched(report, "controller-enrichment-tool") == 4,
          f"P16: the controller's own name and hash in an enrichment's tool list are set aside ({pair.lines()})")
    enrichment(pair.b, "runcontrol.dll", reader="another reader build")
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, rel, "tool[1]/sha256"), f"P16: another tool's hash in the same list is caught ({lines})")
    enrichment(pair.b, "runcontrol.dll", with_hash=False)
    check(names(pair.lines(), rel, "tool[0]/sha256"), "P16: a controller entry with no hash on one side is caught")
    enrichment(pair.a, "othertool.py")
    enrichment(pair.b, "thirdtool.dll")
    lines = pair.lines()
    check(names(lines, rel, "tool[0]/file") and names(lines, rel, "tool[0]/sha256"), f"P16: an entry that is not the controller's is compared ({lines})")


MEMORY_ROUTE = ("memory route: {route} (memory free) - the memory is there; estimated need fast 9.{n} GB (with 25% margin), "
                "lean 1.{n} GB; available to this process 23.{n} GB, limit 66.{n} GB (physical memory)")
MEMORY_BEFORE = "   memory before the host loop: live 0.0{n} GB, this process 0.1{n} GB, free now 22.8{n} GB of 66.1{n} GB; readings from this machine"


def p17(tmp):
    beside = "evaluation/sweep/inv-0001/contactprobe/baseline.json.memory.json"
    log = "evaluation/sweep/inv-0001/logs/contactprobe-baseline.txt"

    def memory(run, n, route="fast", threads=8, cache="2.15", margin="25%"):
        put(run / beside, {"probe": "contactprobe", "route": route, "threads": threads, "cacheBudget": 2147483648,
                           "line": MEMORY_ROUTE.format(route=route, n=n).replace("25%", margin),
                           "needFastBytes": 9489304825 + n, "needLeanBytes": 1468695468 + n, "readingsFrom": "this machine",
                           "readings": {"TotalPhysical": 66095271936, "AvailablePhysical": 22880067584 + n, "OwnPrivate": 123072512 + n,
                                        "ProcessLimit": None, "LimitSource": "none"},
                           "others": [{"Name": "one", "Pid": 100 * n, "Bytes": 5 * n}] * n, "outcome": "completed"})
        put(run / log, MEMORY_BEFORE.format(n=n) + "\n   " + MEMORY_ROUTE.format(route=route, n=n).replace("25%", margin)
            + f"; contact cache {cache} GB  (0.{n}s)\n")
    pair = timed_pair(tmp, "p17", memory)
    report = pair.compare()
    check(report["equal"] and matched(report, "probe-memory-readings") == 16 and matched(report, "probe-memory-lines") >= 6,
          f"P17: contactprobe's memory readings are set aside in the file beside an emit and in its log ({pair.lines()})")
    for label, change, where in (("the route", dict(route="lean"), "route"), ("a setting", dict(threads=4), "threads")):
        pair = Pair(tmp, "p17-" + where)
        memory(pair.a, 1)
        memory(pair.b, 1, **change)
        check(names(pair.lines(), beside, where), f"P17: {label} in the file beside an emit is caught ({pair.lines()})")
    pair = Pair(tmp, "p17-route-line")
    memory(pair.a, 1)
    memory(pair.b, 2, route="lean")
    lines = pair.lines()
    check(names(lines, beside, "line") and names(lines, log, "line 2"), f"P17: the route word inside the memory line is caught in both places ({lines})")
    pair = Pair(tmp, "p17-margin")
    memory(pair.a, 1)
    memory(pair.b, 2, margin="30%")
    lines = pair.lines()
    check(len(lines) == 2 and names(lines, beside, "line") and names(lines, log, "line 2"), f"P17: the margin inside the memory line is caught ({lines})")
    pair = Pair(tmp, "p17-cache")
    memory(pair.a, 1)
    memory(pair.b, 2, cache="4.30")
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, log, "line 2"), f"P17: the cache size on the log's memory line, a setting, is caught ({lines})")
    pair = timed_pair(tmp, "p17-elsewhere", lambda run, n: (
        put(run / "evaluation/logs/check.one__baseline.txt", MEMORY_BEFORE.format(n=n) + "\n"),
        put(run / "evaluation/outputs/check.two/baseline.json.memory.json", {"readings": {"AvailablePhysical": n}})))
    lines = pair.lines()
    check(names(lines, "evaluation/logs/check.one__baseline.txt") and names(lines, "evaluation/outputs/check.two/baseline.json.memory.json"),
          f"P17: the same line and the same file from another probe are compared (the rows name contactprobe only) ({lines})")


def p18(tmp):
    receipt = f"evaluation/receipts/{KEYS[2]}.json"

    def outputs(run, same_second, names_file="candidate-01"):
        # two outputs that differ only in their time of writing: the same bytes when both were written in one second
        hashes = {}
        for i, arm in enumerate(("baseline", "candidate-01")):
            stamp = "2026-11-19T02:50:40Z" if same_second else f"2026-11-19T02:50:4{i}Z"
            hashes[arm] = put(run / f"evaluation/outputs/check.two/{arm}.json", {"probe": "twoprobe", "generatedUtc": stamp, "hits": []})
        edit(run / receipt, lambda d: d.update(outputs=[{"path": "outputs/check.two/candidate-01.json", "sha256": hashes[names_file]
                                                         if names_file in hashes else names_file, "bytes": 70}]))
    for label, a_same, b_same in (("on side A only", True, False), ("on side B only", False, True), ("on both sides", True, True)):
        pair = Pair(tmp, "p18-" + label.replace(" ", "-"))
        outputs(pair.a, a_same)
        outputs(pair.b, b_same)
        check(pair.compare()["equal"], f"P18: two outputs with the same bytes {label}: the hash is named the same way ({pair.lines()})")
    pair = Pair(tmp, "p18-wrong")
    outputs(pair.a, True)
    outputs(pair.b, False, names_file="baseline")
    check(pair.compare()["equal"], "P18: where one side cannot tell the two files apart, a hash of either is the same name")
    pair = Pair(tmp, "p18-wrong-file")
    outputs(pair.a, False)
    outputs(pair.b, False, names_file="baseline")
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, receipt, "outputs[0]/sha256"), f"P18: a hash that names the other file, both sides telling them apart, is caught ({lines})")
    pair = Pair(tmp, "p18-third-file")
    outputs(pair.a, True)
    third = sha((pair.b / "evaluation/outputs/check.one/baseline.json").read_bytes())
    outputs(pair.b, False, names_file=third)
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, receipt, "outputs[0]/sha256") and "check.one/baseline.json" in lines[0],
          f"P18: a hash that names a third file is caught ({lines})")


STOPPED = ("$ runner ordersweep --candidates C\n(stopped at the deadline after 1.{n}s: launched process tree terminated and confirmed gone "
           "(job object terminated; {left} active processes); no completion)\n\n")


def p19(tmp):
    rel = "evaluation/logs/ordersweep.txt"
    pair = timed_pair(tmp, "p19", lambda run, n: put(run / rel, STOPPED.format(n=n, left=0)))
    before = matched(Pair(tmp, "p19-base").compare(), "seconds-command-log")
    report = pair.compare()
    check(report["equal"] and matched(report, "seconds-command-log") == before + 2,
          f"P19: the seconds in the second line of a stopped child's log are set aside ({pair.lines()})")
    pair = Pair(tmp, "p19-rest")
    put(pair.a / rel, STOPPED.format(n=1, left=0))
    put(pair.b / rel, STOPPED.format(n=2, left=1))
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, rel, "line 2"), f"P19: another word of the stop sentence is caught ({lines})")
    put(pair.b / rel, "$ runner ordersweep --candidates C\n(exit 0, 1.2s)\n\n")
    check(names(pair.lines(), rel, "line 2"), "P19: a child that ended against one that was stopped is caught")
    pair = timed_pair(tmp, "p19-elsewhere", lambda run, n: put(run / rel, "$ runner\n(exit 0, 1.0s)\n\n   note: stopped at the deadline after 1.%ds: x)\n" % n))
    check(names(pair.lines(), rel, "line 4"), "P19: the same words inside another line are compared (the row names the whole second-line form)")


def p20(tmp):
    cache_log, sweep_log = "evaluation/sweep/inv-0001/cache-log.txt", "evaluation/logs/ordersweep.txt"

    def stopped_job(run, n, job="oneprobe-candidate-01", stored=True):
        key = sha(f"stored{n}")
        put(run / "evaluation/sweep/inv-0001/cache.json", {key: {"label": "oneprobe-baseline", "fingerprint": "27d21fdbdcef"}} if stored else {})
        missing = sha(f"{job}{n}")[:12]
        put(run / cache_log, f"MISS oneprobe-baseline: no entry for request key {key[:12]}\nRECORDED oneprobe-baseline\n"
            f"MISS {job}: no entry for request key {missing}\n")
        put(run / sweep_log, f"$ runner ordersweep\n(exit 0, 1.0s)\n\n   cache: MISS {job}: no entry for request key {missing}\n")
    pair = timed_pair(tmp, "p20", stopped_job)
    report = pair.compare()
    check(report["equal"] and matched(report, "id-sweep-cache-key") >= 8,
          f"P20: the key of a job stopped before it was stored is named after the job on its cache log line ({pair.lines()})")
    pair = Pair(tmp, "p20-job")
    stopped_job(pair.a, 1)
    stopped_job(pair.b, 2, job="oneprobe-candidate-02")
    lines = pair.lines()
    check(len(lines) == 2 and names(lines, cache_log, "line 3") and names(lines, sweep_log, "line 4"), f"P20: another job on that line is caught ({lines})")
    # a later line that names a key alone points at the job that key was named after
    pair = Pair(tmp, "p20-reference")
    for run, n, resumed in ((pair.a, 1, "oneprobe-candidate-01"), (pair.b, 2, "oneprobe-candidate-02")):
        keys = {job: sha(f"{job}{n}")[:12] for job in ("oneprobe-candidate-01", "oneprobe-candidate-02")}
        put(run / "evaluation/sweep/inv-0001/cache.json", {})
        put(run / cache_log, "".join(f"MISS {job}: no entry for request key {key}\n" for job, key in keys.items())
            + f"resumed at request key {keys[resumed]}\n")
    lines = pair.lines()
    check(len(lines) == 1 and names(lines, cache_log, "line 3") and "<CACHEKEY:oneprobe-candidate-01>" in lines[0]
          and "<CACHEKEY:oneprobe-candidate-02>" in lines[0], f"P20: a line that names the unstored key of another job is caught ({lines})")
    pair = Pair(tmp, "p20-other-line")
    stopped_job(pair.a, 1)
    stopped_job(pair.b, 2)
    append(pair.a / cache_log, "HIT oneprobe-candidate-02: request key 0123456789ab\n")
    append(pair.b / cache_log, "HIT oneprobe-candidate-02: request key ba9876543210\n")
    check(names(pair.lines(), cache_log, "line 4"), "P20: a key on a line of another shape, with no entry and no job to name it, is compared")
    pair = Pair(tmp, "p20-elsewhere")
    for run, n in ((pair.a, 1), (pair.b, 2)):
        put(run / "evaluation/logs/check.one__baseline.txt", f"MISS oneprobe-candidate-01: no entry for request key {sha(str(n))[:12]}\n")
    check(names(pair.lines(), "evaluation/logs/check.one__baseline.txt"), "P20: the same line in another log is compared (the row names the cache log and the sweep's log)")


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="controller-parity-"))
    try:
        for label, fn in (("P1", p1), ("P2", p2), ("P3", p3), ("P4", p4), ("P5", p5), ("P6", p6), ("P7", p7), ("P8", p8),
                          ("P9", p9), ("P10", p10), ("P11", p11), ("P12", p12), ("P13", p13), ("P14", p14),
                          ("P15", p15), ("P16", p16), ("P17", p17), ("P18", p18), ("P19", p19), ("P20", p20)):
            guarded(label, lambda fn=fn: fn(tmp))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
