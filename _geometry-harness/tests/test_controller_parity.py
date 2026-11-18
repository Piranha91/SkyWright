"""Known answers for controller_parity.py, the tool that compares two run controllers' run folders.

    python _geometry-harness/tests/test_controller_parity.py

Each case builds two small run folders by hand in a temporary folder. The two hold the same evaluation but differ in
everything a rerun changes: the run folder's path, the checkout path, the plan id, receipt ids, run ids, times and
durations. P1 the two compare equal; P2 each planted kind of difference is caught and named; P3 a ledger entry that
points at another receipt's id is caught; P4 a field the tool has never heard of is compared; P5 a probe build hash
is compared between runs of one checkout and not between two checkouts; P6 only a list the table names is compared as
a bag; P7 a time that exists on one side only is caught; P8 a whole number against a decimal is caught; P9 a recorded
hash that names the wrong run file is caught; P10 a file only one side wrote is caught; P11 the exclusion table is well
formed and printed in full; P12 the command's exit codes. No probe, no controller and no game is started.
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
    actions = {"ignore", "present-only", "normalise-path", "normalise-id", "unordered"}
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


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="controller-parity-"))
    try:
        for label, fn in (("P1", p1), ("P2", p2), ("P3", p3), ("P4", p4), ("P5", p5), ("P6", p6), ("P7", p7), ("P8", p8),
                          ("P9", p9), ("P10", p10), ("P11", p11), ("P12", p12)):
            guarded(label, lambda fn=fn: fn(tmp))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
