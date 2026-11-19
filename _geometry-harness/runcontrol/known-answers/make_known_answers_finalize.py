"""Write finalize.json: the known answers for the run-folder model and for `runcontrol finalize` / `verify-bundle`,
computed by evalrun.py.

Every expected value comes from the real module: load_plan and the plan refusals, the Ledger, write_receipt,
same_account, read_receipts, dependency_keys, receipt_roles, argv_shape, identity_defects, identity_guarantee, and (the
later sections) order_classification, evidence_integrity, build_disposition, status_md, cmd_finalize and
cmd_verify_bundle. The clock is replaced by a counter so every byte is repeatable. Run it again only when one of those
rules changes on purpose, and commit the result with the change:

    python _geometry-harness/runcontrol/known-answers/make_known_answers_finalize.py

Nothing outside a temporary folder is read or written except the harness sources and the output.
"""
from __future__ import annotations

import base64
import contextlib
import copy
import hashlib
import io
import json
import os
import platform
import shutil
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parents[1]
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(HERE.parents[2] / "scripts"))
from runlog import Run   # noqa: E402
import evalrun as E      # noqa: E402

OUT = HERE / "finalize.json"
BACK, FWD, DOUBLED = "<ROOT\\>", "<ROOT/>", "<ROOT\\\\>"


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii")


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def mask(obj, root: Path):
    """The same value with the temporary root replaced by a placeholder: as written, with forward slashes, and with
    every backslash doubled (how repr() and JSON show it)."""
    if isinstance(obj, str):
        doubled = str(root).replace("\\", "\\\\")
        return obj.replace(doubled, DOUBLED).replace(str(root), BACK).replace(str(root).replace("\\", "/"), FWD)
    if isinstance(obj, dict):
        return {mask(k, root): mask(v, root) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [mask(v, root) for v in obj]
    return obj


def write(root: Path, rel: str, data: bytes | str) -> Path:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(data.encode("utf-8") if isinstance(data, str) else data)
    return p


def jtext(obj) -> str:
    """A file as evalrun's write_json leaves it on this platform."""
    return json.dumps(obj, indent=1, ensure_ascii=False).replace("\n", os.linesep)


def tree(root: Path, masked: tuple = ()) -> list:
    """Every file under root: its bytes, or (for a name in `masked`) its text with the root replaced."""
    rows = []
    for p in sorted(root.rglob("*")):
        if not p.is_file():
            continue
        rel = p.relative_to(root).as_posix()
        if p.name in masked:
            rows.append({"path": rel, "text": mask(p.read_bytes().decode("utf-8"), root)})
        else:
            rows.append({"path": rel, "bytes": b64(p.read_bytes())})
    return rows


class Clock:
    """evalrun.utcnow replaced by a counter: one second later on every call."""

    def __init__(self):
        self.n = 0

    def __call__(self) -> str:
        self.n += 1
        return f"2026-01-01T00:{self.n // 60:02d}:{self.n % 60:02d}Z"


@contextlib.contextmanager
def fake_clock():
    real = E.utcnow
    E.utcnow = Clock()
    try:
        yield
    finally:
        E.utcnow = real


@contextlib.contextmanager
def captured():
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        yield out, err


@contextlib.contextmanager
def cwd(path: Path):
    before = os.getcwd()
    os.chdir(path)
    try:
        yield
    finally:
        os.chdir(before)


# ------------------------------------------------------------------------------------------ small plans

def programs(*pids, digest="folder:" + "a" * 64) -> dict:
    return {pid: {"kind": "package", "state": "present", "digest": digest if pid != "controller" else "scripts:" + "c" * 64}
            for pid in pids}


def mini_plan(**over) -> dict:
    plan = {"schemaVersion": 2, "planId": "planaaaaaaaa", "requestProviders": "arm-view",
            "registry": {"path": "registry.json", "registryVersion": 7, "sha256": "r" * 64},
            "toolIdentity": {"contract": 1, "rule": "x", "programs": programs("controller", "probe:overlapprobe",
                                                                             "helper:conflictdiff", "probe:depprobe")},
            "arms": [{"armId": "baseline", "kind": "baseline"}, {"armId": "candidate-01", "kind": "candidate"}],
            "checks": [{"checkId": "g.overlap", "prerequisites": ["l.masters"]}, {"checkId": "l.masters"},
                       {"checkId": "p.order", "prerequisites": []}]}
    plan.update(over)
    return plan


# ------------------------------------------------------------------------------------------ plan loading

def plan_cases(tmp: Path) -> dict:
    load = []

    def case(name, build):
        root = tmp / "load" / name
        root.mkdir(parents=True)
        build(root)
        plan, why = E.load_plan(root)
        load.append({"name": name, "tree": tree(root), "plan": plan is not None, "why": mask(why, root)})

    def good(root, doc=None, text=None):
        body = text if text is not None else jtext(doc or mini_plan())
        write(root, "plan.json", body)
        write(root, "plan.sha256", sha(body.encode("utf-8")) + "\n")

    case("no plan", lambda r: None)
    case("no hash", lambda r: write(r, "plan.json", "{}"))
    case("hash differs", lambda r: (write(r, "plan.json", "{}"), write(r, "plan.sha256", "0" * 64 + "\n")))
    case("good", good)
    case("hash with spaces", lambda r: (write(r, "plan.json", jtext(mini_plan())),
                                        write(r, "plan.sha256", "  " + sha(jtext(mini_plan()).encode()) + " \r\n")))
    case("not json", lambda r: good(r, text="{not json"))
    case("a list", lambda r: good(r, text="[1, 2]"))
    case("schema 1", lambda r: good(r, mini_plan(schemaVersion=1)))
    case("schema text", lambda r: good(r, mini_plan(schemaVersion="2")))
    case("schema float", lambda r: good(r, text='{"schemaVersion": 2.0, "planId": "p"}'))
    case("schema absent", lambda r: good(r, text='{"planId": "p"}'))

    request = [{"plan": p, "want": E.request_refusal(p)} for p in (
        {"requestProviders": "arm-view"}, {}, {"requestProviders": "scope"}, {"requestProviders": None},
        {"requestProviders": ["arm-view"]}, {"requestProviders": "it's"})]
    cause = [{"why": w, "want": E.refusal_cause(w)} for w in (
        "plan-missing: x", "plan-changed: plan.json no longer", "registry-changed: C:\\x now hashes", None, "",
        "Not a cause: x", " scope-missing : y", "x", "x1: y", "-x: y", "a-b-: z", "C:\\run\\ledger.json is missing or corrupt.",
        "foreign-run: a: b")]
    contract = [{"plan": p, "want": E.identity_contract_refusal(p)} for p in (
        mini_plan(), {}, {"toolIdentity": None}, {"toolIdentity": {"contract": 1}}, {"toolIdentity": {"contract": 2, "programs": {}}},
        {"toolIdentity": {"contract": 1, "programs": {}}}, {"toolIdentity": {"contract": 1.0, "programs": {}}},
        {"toolIdentity": {"contract": True, "programs": {}}}, {"toolIdentity": {"contract": 1, "programs": []}})]

    # premises: the registry and scope files a plan names, present, changed, missing and relative
    premise = []
    root = tmp / "premise"
    reg = write(root, "registry.json", '{"registryVersion": 7}')
    scope = write(root, "scope.json", '{"complete": true}')
    write(root, "elsewhere/rel-registry.json", '{"registryVersion": 7}')
    base = {"registry": {"path": str(reg), "sha256": sha(reg.read_bytes())},
            "scope": {"path": str(scope), "sha256": sha(scope.read_bytes())},
            "candidatesDir": str(root / "candidates"),
            "arms": [{"armId": "baseline", "candidatePath": str(root / "candidates" / "c0.json")}]}

    def premise_case(name, plan, only=None, remedy=None):
        with cwd(root):
            premise.append({"name": name, "plan": mask(plan, root), "only": only, "remedy": remedy,
                            "premise": mask(E.premise_refusal(plan), root) if only is None and remedy is None else None,
                            "relative": mask(E.relative_path_refusal(plan, only=tuple(only) if only is not None else None,
                                                                     remedy=remedy), root)})

    def changed(**over):
        p = copy.deepcopy(base)
        for k, v in over.items():
            if isinstance(v, dict) and isinstance(p.get(k), dict):
                p[k].update(v)
            else:
                p[k] = v
        return p

    premise_case("all present", base)
    premise_case("registry changed", changed(registry={"sha256": "0" * 64}))
    premise_case("registry gone", changed(registry={"path": str(root / "gone.json")}))
    premise_case("registry hash absent", {"registry": {"path": str(reg)}, "arms": []})
    premise_case("scope changed", changed(scope={"sha256": "1" * 64}))
    premise_case("scope gone", changed(scope={"path": str(root / "no-scope.json")}))
    premise_case("no scope", changed(scope={"path": None, "sha256": None}))
    premise_case("scope absent", {k: v for k, v in base.items() if k != "scope"})
    premise_case("relative registry that resolves", changed(registry={"path": "registry.json"}))
    premise_case("relative registry that does not", changed(registry={"path": "elsewhere-not/registry.json"}))
    premise_case("relative scope", changed(scope={"path": "nowhere/scope.json"}))
    premise_case("relative candidates folder", changed(candidatesDir="cands"))
    premise_case("relative candidate", changed(arms=[{"armId": "candidate-03", "candidatePath": "cands/c3.json"}]))
    premise_case("relative, limited to scope", changed(registry={"path": "x/registry.json"}, scope={"path": "y/scope.json"}),
                 only=["scope"], remedy="pass --scope naming a copy that hashes 123.")
    premise_case("relative, limited to registry", changed(registry={"path": "x/registry.json"}), only=["registry"])
    premise_case("forward slashes", changed(registry={"path": str(reg).replace("\\", "/")}))
    named = []
    for label, file, nxt in (("registry", reg, "Next."), ("scope", scope, "Go."), ("registry", root / "gone.json", "Next."),
                             ("scope", reg, "Other.")):
        named.append({"label": label, "named": mask(str(file), root), "nxt": nxt, "plan": mask(base, root),
                      "want": mask(E.premise_file_refusal(base, label, nxt, named=str(file)), root)})
    return {"load": load, "request": request, "cause": cause, "contract": contract,
            "premise": {"tree": tree(root), "cases": premise, "named": named}}


# ------------------------------------------------------------------------------------------------ ledger

def ledger_script(tmp: Path, name: str, plan: dict, ops: list) -> dict:
    """One scripted life of a ledger: each step's answer, the two files' hashes after it, and their final bytes."""
    root = tmp / "ledger" / name
    root.mkdir(parents=True)
    steps = []
    led = None
    with fake_clock():
        for op in ops:
            kind, want = op[0], None
            if kind == "write":
                write(root, op[1], op[2])
            elif kind == "new":
                led = E.Ledger(root, plan)
                want = led.doc is not None
            elif kind == "refusal":
                want = mask(led.refusal(), root)
            elif kind == "open":
                led.open_invocation(op[1], op[2], op[3], **(op[4] if len(op) > 4 else {}))
            elif kind == "transition":
                led.transition(op[1], op[2], op[3], op[4], **(op[5] if len(op) > 5 else {}))
            elif kind == "heal":
                want = led.heal_interrupted()
            elif kind == "rebuild":
                led.rebuild()
            elif kind == "entry":
                e = led.entry(op[1], op[2], op[3])
                want = None if e is None else json.dumps(e)
            elif kind == "facts":
                want = json.dumps(led.receipt_facts(op[1], op[2]))
            else:
                raise ValueError(kind)
            files = {n: (sha((root / n).read_bytes()) if (root / n).exists() else None) for n in ("ledger.json", "journal.jsonl")}
            steps.append({"op": op, "want": want, "files": files})
    return {"name": name, "plan": plan, "steps": steps, "tree": tree(root)}


def ledger_cases(tmp: Path) -> list:
    plan = mini_plan()
    empty = jtext({"schemaVersion": 2, "planId": plan["planId"], "registrySha256": "r" * 64, "runIds": [], "entries": {}})
    receipt = {"receiptId": "rid-1", "planId": plan["planId"], "checkId": "g.overlap", "armId": "baseline", "part": "arm",
               "result": {"detector": "no-finding"}, "validation": {"accepted": 1, "rejections": None},
               "imported": {"origin": "fresh"}}
    scripts = [
        ("missing", [["new"], ["refusal"]]),
        ("corrupt", [["write", "ledger.json", "{oops"], ["new"], ["refusal"]]),
        ("foreign", [["write", "ledger.json", jtext({"planId": "other", "entries": {}})], ["new"], ["refusal"]]),
        ("no plan id", [["write", "ledger.json", jtext({"entries": {}})], ["new"], ["refusal"]]),
        ("life", [
            ["write", "ledger.json", empty], ["write", "journal.jsonl", ""], ["new"], ["refusal"],
            ["open", "run", "evalrun-checks-1", None],
            ["transition", "l.masters", "baseline", None, "running"],
            ["transition", "l.masters", "baseline", None, "completed",
             {"receipt_id": "abc", "detector": "no-finding", "detail": "ok \u00e9 \u2028 line", "next_action": None}],
            ["entry", "l.masters", "baseline", None],
            ["transition", "g.overlap", "candidate-01", "diff", "rejected(exit-nonzero)",
             {"cause": "exit-nonzero", "receipt_id": "def", "rejections": [{"cause": "exit-nonzero", "detail": "exit 3"}],
              "detail": "conflictdiff said \"no\"", "next_action": "resume"}],
            ["transition", "g.overlap", "baseline", "arm", "completed",
             {"receipt_id": "r1", "detector": "finding", "extra": {"reusedFromRunId": "old", "derivedFrom": ["a"], "sweepFolder": "sweep/inv-0001"}}],
            ["transition", "g.overlap", "baseline", "arm", "reused", {"receipt_id": "r1", "detector": "finding"}],
            ["transition", "g.overlap", "baseline", "arm", "reused",
             {"receipt_id": "r1", "detector": "finding", "extra": {"reusedFromRunId": "newer", "imported": None}}],
            ["transition", "p.order", "baseline", None, "completed",
             {"receipt_id": "i1", "detector": "catalogue", "extra": {"imported": True, "importOrigin": "fresh", "checkId": "overridden"}}],
            ["transition", "p.order", "baseline", None, "invalidated(the receipt file is gone)", {"cause": "stale-inputs", "rejections": []}],
            ["transition", "l.masters", "candidate-01", None, "running"],
            ["transition", "g.overlap", "candidate-01", "arm", "running"],
            ["open", "resume", "evalrun-checks-2", "parent-1", {"budget_minutes": 12.5}],
            ["open", "resume", None, None, {"budget_minutes": 0}],
            ["heal"],
            ["entry", "l.masters", "candidate-01", None],
            ["entry", "nothing", "here", None],
        ]),
        ("rebuild", [
            ["write", "journal.jsonl",
             "\r\n".join([
                 E.canonical({"t": "T1", "checkId": "g.overlap", "armId": "baseline", "part": "arm", "from": None, "to": "running",
                              "cause": None, "receiptId": None, "detail": None, "nextAction": None, "runId": "run-1"}),
                 "", "   ", "{broken",
                 E.canonical({"t": "T2", "checkId": "g.overlap", "armId": "baseline", "part": "arm", "from": "running",
                              "to": "completed", "cause": None, "receiptId": "rid-1", "detail": "d", "nextAction": None, "runId": "run-1"}),
                 E.canonical({"t": "T3", "checkId": "l.masters", "armId": "baseline", "part": None, "to": "completed",
                              "receiptId": "rid-other", "runId": "run-1"}),
                 E.canonical({"t": "T4", "armId": "candidate-01", "to": "manual"}),
                 E.canonical({"t": "T5", "checkId": "p.order", "armId": "baseline", "part": "", "to": "completed", "receiptId": "rid-9"}),
             ]) + "\r\n"],
            ["write", "receipts/g.overlap__baseline__arm.json", jtext(receipt)],
            ["write", "receipts/l.masters__baseline.json", jtext(dict(receipt, checkId="l.masters", part=None))],
            ["write", "receipts/p.order__baseline.json",
             jtext({"receiptId": "rid-9", "planId": plan["planId"], "checkId": "p.order", "armId": "baseline", "part": "",
                    "result": None, "validation": {"rejections": [{"cause": "x"}]}, "imported": "yes"})],
            ["new"], ["rebuild"], ["refusal"],
            ["facts", "g.overlap__baseline__arm", {"receiptId": None}],
            ["facts", "g.overlap__baseline__arm", {"receiptId": "rid-1", "checkId": "g.overlap", "armId": "baseline", "part": "arm", "status": "reused"}],
            ["facts", "g.overlap__baseline__arm", {"receiptId": "rid-1", "checkId": "g.overlap", "armId": "baseline", "part": None}],
            ["facts", "no__receipt", {"receiptId": "rid-1"}],
        ]),
        ("rebuild without a journal", [["new"], ["rebuild"], ["refusal"]]),
    ]
    out = [ledger_script(tmp, name, plan, ops) for name, ops in scripts]
    keys = [{"args": list(a), "want": E.Ledger.key(*a)} for a in (
        ("c", "a", None), ("c", "a", "arm"), ("c", "a", ""), (None, None, None), ("geometry.object-overlap", "candidate-01", "diff"))]
    return {"scripts": out, "keys": keys}


# ---------------------------------------------------------------------------------------------- receipts

def receipt_cases(tmp: Path) -> dict:
    root = tmp / "receipts"
    run = root / "run"
    plan = mini_plan()
    check, arm = {"checkId": "g.overlap"}, {"armId": "candidate-01"}
    out1 = write(run, "outputs/g.overlap/candidate-01.json", '{"Probe": "overlapprobe"}')
    out2 = write(root, "outside/else.json", "outside the run")
    log = write(run, "logs/g.overlap__candidate-01.txt", "$ x\n(exit 0, 0.1s)\n")
    stamps = {out1: 1_760_000_000_123_456_700, out2: 1_700_000_000_999_999_900, log: 1_760_000_001_000_000_000}
    for p, ns in stamps.items():
        os.utime(p, ns=(ns, ns))
    files = [{"path": p.relative_to(root).as_posix(), "bytes": b64(p.read_bytes()), "mtimeNs": ns} for p, ns in stamps.items()]
    adapter = {"kind": "probe-emit", "exe": "overlapprobe"}
    calls = [
        {"name": "a probe receipt", "part": "arm", "adapter": adapter,
         "argv": [str(root / "tools" / "overlapprobe.exe"), "--order", str(run / "candidates" / "c1.json"), "--emit", str(out1),
                  "--instance", "X:/somewhere/else", "--flag", "./relative", ".\\dot", "..\\up", "plain", "/rooted"],
         "runId": "run-1", "parent": "parent-1", "started": "2026-01-01T00:00:00Z", "ended": "2026-01-01T00:00:09Z", "exit": 0,
         "outputs": [str(out1), str(run / "outputs" / "g.overlap" / "missing.json"), str(out2)], "log": str(log),
         "consumed": {"candidateSha256": "c" * 64, "programs": {"emitter": {"program": "probe:overlapprobe"}}},
         "validation": {"accepted": True, "rejections": [], "identityLimits": [{"plugin": "A.esp"}], "detector": "dropped"},
         "result": {"execution": "completed", "detector": "finding", "findings": 2, "coverage": [], "strength": "measured", "verdict": None},
         "extra": {"sweep": {"folder": "sweep/inv-0001"}, "part": "overridden-by-extra"}},
        {"name": "an accounting receipt", "part": None, "adapter": {"kind": "manual"}, "argv": None, "runId": None, "parent": None,
         "started": None, "ended": "2026-01-01T00:00:10Z", "exit": None, "outputs": [], "log": str(run / "logs" / "absent.txt"),
         "consumed": {}, "validation": {"accepted": 0}, "result": {"execution": "manual", "detector": None}, "extra": None},
        {"name": "the same account again", "part": None, "adapter": {"kind": "manual"}, "argv": [], "runId": None, "parent": "p2",
         "started": None, "ended": "2026-01-01T00:00:20Z", "exit": None, "outputs": [], "log": None,
         "consumed": {}, "validation": {"accepted": 0}, "result": {"execution": "manual", "detector": None}, "extra": None,
         "keepSame": True},
        {"name": "a changed account", "part": None, "adapter": {"kind": "manual"}, "argv": None, "runId": None, "parent": "p3",
         "started": None, "ended": "2026-01-01T00:00:30Z", "exit": None, "outputs": [], "log": None,
         "consumed": {}, "validation": {"accepted": 0}, "result": {"execution": "manual", "detector": None, "note": "new"},
         "extra": None, "keepSame": True},
    ]
    with fake_clock():
        for c in calls:
            r = E.write_receipt(run, plan=plan, check=check, arm=arm, part=c["part"], adapter=c["adapter"], argv=c["argv"],
                                run_id=c["runId"], parent_run_id=c["parent"], started=c["started"], ended=c["ended"],
                                exit_code=c["exit"], outputs=[Path(p) for p in c["outputs"]],
                                log=Path(c["log"]) if c["log"] else None, consumed=c["consumed"], validation=c["validation"],
                                result=c["result"], extra=c["extra"], keep_same=c.get("keepSame", False))
            key = E.Ledger.key(check["checkId"], arm["armId"], c["part"])
            c["returned"] = mask(json.dumps(r, indent=1, ensure_ascii=False), root)
            c["file"] = mask((run / "receipts" / f"{key}.json").read_bytes().decode("utf-8"), root)
            c["key"] = key
            for k in ("argv", "outputs", "log"):
                c[k] = mask(c[k], root)

    a = {"planId": "p", "checkId": "c", "armId": "a", "part": None, "receiptId": "1", "endedUtc": "t1", "parentRunId": "x",
         "result": {"n": 1, "f": 1.0}, "outputs": []}
    same = [{"prev": p, "new": n, "want": E.same_account(p, n)} for p, n in (
        (a, dict(a, receiptId="2", endedUtc="t2", parentRunId=None)), (a, dict(a, part="arm")), (a, dict(a, result={"n": 2, "f": 1.0})),
        (None, a), ([a], a), (a, dict(a, extra=1)), (dict(a, result={"f": 1.0, "n": 1}), a), (a, dict(a, result={"n": 1, "f": 1})),
        (dict(a, planId="q"), dict(a, planId="q")), (a, dict(a, planId="q")), (dict(a, part=""), a))]

    order_root = root / "order"
    names = ["b__x.json", "a__x__arm.json", "a__x.json", "A__w.json", "a__x__diff.JSON", "note.txt", "c__y.json.tmp", "z.json",
             "a.b__x.json", "a-b__x.json", "a_b__x.json", "broken.json", "list.json", "\u00e9__x.json"]
    for n in names:
        body = "{oops" if n == "broken.json" else "[1]" if n == "list.json" else json.dumps({"receiptId": n})
        write(order_root, "receipts/" + n, body)
    (order_root / "receipts" / "folder.json").mkdir()
    read = {"tree": tree(order_root), "dirs": ["receipts/folder.json"], "keys": list(E.read_receipts(order_root)),
            "none": list(E.read_receipts(root / "no-such-run"))}

    roles = [{"receipt": r, "want": [[k, v] for k, v in E.receipt_roles(r).items()]} for r in (
        {"adapter": {"kind": "ordersweep-probe", "exe": "overlapprobe"}, "part": "diff"},
        {"adapter": {"kind": "ordersweep-probe", "exe": "overlapprobe"}, "part": "arm"},
        {"adapter": {"kind": "probe-json", "exe": "depprobe"}}, {"adapter": {"kind": "probe-json"}},
        {"adapter": {"kind": "patchprobe", "exe": "conflictdiff"}}, {"adapter": {"kind": "derived", "exe": "x"}},
        {"adapter": None}, {}, {"adapter": {"kind": "mcp-only"}, "part": "diff"})]
    labels = [{"pid": p, "want": E.program_label(p), "id": E.program_id(p)} for p in (
        "controller", "probe:overlapprobe", "helper:conflictdiff", "odd", "conflictdiff", "x:y:z")]

    shape_run = root / "run"
    shapes = []
    with cwd(root):
        for argv in (
                [str(shape_run / "outputs" / "a.json"), str(shape_run), str(root / "x.json"), "C:/other/place/file.esp",
                 "--flag", "name.json", "./run/rel.json", ".\\run\\sub\\rel.json", "./notrun/rel.json", "\\rooted\\file.txt",
                 "/also/rooted", "C:relative.txt", str(shape_run / "sub" / ".." / "up.json"), str(root) + "\\RUN\\SUB\\Case.json",
                 "", "..", "./.."],):
            shapes.append({"argv": mask(argv, root), "want": E.argv_shape(argv, shape_run)})

    dep_plan = mini_plan(arms=[{"armId": "b0", "kind": "baseline"}, {"armId": "c1", "kind": "candidate"}])
    dep_receipts = {"p.order__c1": {"receiptId": "d1", "derivedFrom": ["r-arm", "r-diff", "nobody", "r-arm"]},
                    "g.overlap__c1__arm": {"receiptId": "r-arm"}, "g.overlap__c1__diff": {"receiptId": "r-diff"},
                    "other__c1": {"receiptId": "r-arm"}, "none__c1": {}}
    deps = []
    for plan_, entry in (
            (dep_plan, {"checkId": "g.overlap", "armId": "c1", "part": "diff"}),
            (dep_plan, {"checkId": "g.overlap", "armId": "c1", "part": "reverse"}),
            (dep_plan, {"checkId": "g.overlap", "armId": "c1", "part": "arm"}),
            (dep_plan, {"checkId": "g.overlap", "armId": "b0", "part": "arm"}),
            (dep_plan, {"checkId": "g.overlap", "armId": "c1"}),
            (dep_plan, {"checkId": "p.order", "armId": "c1", "part": None}),
            (dep_plan, {"checkId": "unknown", "armId": "c1", "part": "diff"}),
            (mini_plan(arms=[{"armId": "x", "kind": "candidate"}]), {"checkId": "g.overlap", "armId": "x", "part": "arm"}),
            (mini_plan(arms=[{"armId": "x", "kind": "candidate"}]), {"checkId": "g.overlap", "armId": "baseline", "part": "diff"})):
        deps.append({"plan": plan_, "entry": entry, "want": E.dependency_keys(plan_, dep_receipts, entry)})

    return {"files": files, "calls": calls, "plan": plan, "check": check, "arm": arm, "same": same, "read": read,
            "roles": roles, "labels": labels, "shapes": shapes, "dependencies": {"receipts": dep_receipts, "cases": deps}}


# ---------------------------------------------------------------------------------------------- identity

def identity_cases() -> dict:
    plan = mini_plan()
    legacy = {k: v for k, v in mini_plan().items() if k != "toolIdentity"}
    absent = mini_plan()
    absent["toolIdentity"]["programs"]["probe:depprobe"] = {"kind": "package", "state": "absent", "digest": None}
    absent["toolIdentity"]["programs"]["probe:overlapprobe"] = "not a dict"
    good = "folder:" + "a" * 64
    ctl = "scripts:" + "c" * 64

    def rec(kind, exe, part, bound):
        return {"adapter": {"kind": kind, "exe": exe}, "part": part, "consumed": {"programs": bound}}

    def row(pid, digest):
        return {"program": pid, "kind": "package", "digest": digest, "checked": ["launch", "acceptance"]}

    receipts = [
        rec("probe-json", "depprobe", None, {"emitter": row("probe:depprobe", good), "controller": row("controller", ctl)}),
        rec("probe-json", "depprobe", None, {"emitter": row("probe:depprobe", "folder:" + "b" * 64), "controller": row("controller", ctl)}),
        rec("probe-json", "depprobe", None, {"controller": row("controller", ctl)}),
        rec("probe-json", "depprobe", None, {"emitter": row("probe:other", good), "controller": row("controller", "")}),
        rec("probe-json", "depprobe", None, None),
        rec("probe-json", "depprobe", None, "text"),
        rec("probe-json", "depprobe", None, {"emitter": "text", "controller": {"program": "controller", "digest": 7}}),
        rec("ordersweep-probe", "overlapprobe", "diff", {"comparator": row("helper:conflictdiff", good), "controller": row("controller", ctl)}),
        rec("ordersweep-probe", "overlapprobe", "diff", {"emitter": row("probe:overlapprobe", good), "controller": row("controller", ctl)}),
        rec("ordersweep-probe", "overlapprobe", "arm", {"emitter": row("probe:overlapprobe", good), "controller": row("controller", ctl)}),
        rec("derived", None, None, {"controller": row("controller", ctl)}),
        rec("probe-json", "unknownprobe", None, {"emitter": row("probe:unknownprobe", good), "controller": row("controller", ctl)}),
        {"adapter": {"kind": "manual"}},
    ]
    cases = []
    for name, p in (("contract", plan), ("legacy", legacy), ("absent and odd records", absent)):
        for r in receipts:
            cases.append({"plan": name, "receipt": r, "want": [list(t) for t in E.identity_defects(p, r)]})
    return {"plans": {"contract": plan, "legacy": legacy, "absent and odd records": absent}, "cases": cases,
            "guarantee": {name: E.identity_guarantee(p) for name, p in (("contract", plan), ("legacy", legacy), ("empty", {}))}}


SECTIONS = [("plans", plan_cases), ("ledger", ledger_cases), ("receipts", receipt_cases), ("identity", lambda tmp: identity_cases())]


def main() -> int:
    if os.name != "nt":
        print("make_known_answers_finalize.py records Windows path rules; run it on Windows.", file=sys.stderr)
        return 2
    with Run("make-known-answers-finalize", stage_source="single-stage"):
        return write_answers()


def write_answers() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-known-finalize-")).resolve()
    try:
        doc = {"python": platform.python_version(), "placeholders": {"backslash": BACK, "forward": FWD, "doubled": DOUBLED}}
        for name, build in SECTIONS:
            doc[name] = build(tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
