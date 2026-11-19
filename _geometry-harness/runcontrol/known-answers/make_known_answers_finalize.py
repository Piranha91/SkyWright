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
import re
import shutil
import sys
import tempfile
import zlib
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


def pack(text):
    """A long text kept deflated ({"z": base64}); a short one, or None, as it is."""
    if text is None or len(text) < 600:
        return text
    return {"z": b64(zlib.compress(text.encode("utf-8"), 9))}


def packb(data: bytes):
    """A file's bytes as base64, deflated first when long."""
    return b64(data) if len(data) < 600 else {"z": b64(zlib.compress(data, 9))}


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
            rows.append({"path": rel, "text": pack(mask(p.read_bytes().decode("utf-8"), root))})
        else:
            rows.append({"path": rel, "bytes": packb(p.read_bytes())})
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


# ------------------------------------------------------------------------------- synthetic run folders

GOOD = "folder:" + "a" * 64
CTL = "scripts:" + "c" * 64


def check_row(cid, kind, *, exe=None, family="geometry", required=True, applicability="yes",
              arms=("baseline", "candidate-01"), result=None, **more) -> dict:
    adapter = {"kind": kind}
    if exe:
        adapter["exe"] = exe
    if result:
        adapter["result"] = result
    row = {"checkId": cid, "family": family, "required": required, "applicability": applicability,
           "applicabilityEvidence": "the scope lists a changed cell", "adapter": adapter,
           "arms": [{"armId": a, "status": "planned"} for a in arms], "prerequisites": []}
    row.update(more)
    return row


def run_plan(root: Path, checks: list, *, arms=None, **over) -> dict:
    """A plan for a synthetic run folder: its premises are files inside the folder, named by absolute path."""
    reg = write(root, "premises/registry.json", '{"registryVersion": 7}')
    scope = write(root, "premises/scope.json", '{"complete": true}')
    arms = arms or [
        {"armId": "baseline", "kind": "baseline", "candidateFile": "candidate-00-baseline.json", "loadedInGameNote": "not loaded",
         "payload": {"espOnly": False}, "plugins": ["Skyrim.esm", "A.esp", "B.esp", "Target.esp"],
         "candidatePath": str(root / "candidates" / "candidate-00-baseline.json")},
        {"armId": "candidate-01", "kind": "candidate", "candidateFile": "candidate-01-flip.json", "loadedInGameNote": "not loaded",
         "payload": {"espOnly": False}, "plugins": ["Skyrim.esm", "B.esp", "A.esp", "Target.esp"],
         "candidatePath": str(root / "candidates" / "candidate-01-flip.json")}]
    plan = {"schemaVersion": 2, "planId": "plan00000001", "requestProviders": "arm-view", "createdUtc": "2026-01-01T00:00:00Z",
            "registry": {"path": str(reg), "registryVersion": 7, "sha256": sha(reg.read_bytes())},
            "toolIdentity": {"contract": 1, "rule": "x", "programs": {
                "controller": {"kind": "scripts", "state": "present", "digest": CTL},
                **{f"probe:{n}": {"kind": "package", "state": "present", "digest": GOOD}
                   for n in ("scopeprobe", "overlapprobe", "contactprobe", "depprobe", "patchprobe", "adviceprobe", "stateprobe")},
                "helper:conflictdiff": {"kind": "package", "state": "present", "digest": GOOD}}},
            "targets": [{"name": "Target.esp", "nexusId": 1234}, "odd", {"provider": "no name"}],
            "scope": {"path": str(scope), "sha256": sha(scope.read_bytes()), "complete": True, "unmeasured": ["LAND extents"]},
            "candidatesDir": str(root / "candidates"),
            "arms": arms,
            "states": {"selected": ["baseline"], "depth": "standard", "notExplored": [{"state": "quest-done", "why": "not selected"}]},
            "checks": checks,
            "budgets": {"minutes": 90.0, "maxCandidates": 4, "candidatesHeldBack": []},
            "explicitDecisions": {"select": None, "exclude": [], "note": "nothing narrowed"}}
    plan.update(over)
    return plan


class Builder:
    """A run folder written by the real Ledger and write_receipt, one settled entry at a time."""

    def __init__(self, root: Path, plan: dict):
        self.root, self.plan = root, plan
        body = jtext(plan)
        write(root, "plan.json", body)
        write(root, "plan.sha256", sha(body.encode("utf-8")) + "\n")
        write(root, "ledger.json", jtext({"schemaVersion": 2, "planId": plan["planId"], "registrySha256": plan["registry"]["sha256"],
                                          "runIds": [], "entries": {}}))
        write(root, "journal.jsonl", "")
        self.led = E.Ledger(root, plan)
        self.led.open_invocation("run", "evalrun-checks-1", None)
        self.checks = {c["checkId"]: c for c in plan["checks"]}

    def bound(self, adapter: dict, part) -> dict:
        roles = E.receipt_roles({"adapter": adapter, "part": part})
        progs = (self.plan.get("toolIdentity") or {}).get("programs") or {}
        return {role: {"program": pid, "kind": "package", "digest": (progs.get(pid) or {}).get("digest"), "checked": ["launch", "acceptance"]}
                for role, pid in roles.items()}

    def settle(self, cid, aid, part, status, *, detector=None, cause=None, accepted=True, outputs=(), result=None,
               consumed=None, extra=None, rejections=None, ledger_extra=None, receipt=True, detail=None, adapter=None,
               ledger_detector="same"):
        check = self.checks.get(cid) or {"checkId": cid, "adapter": {"kind": "derived"}}
        adapter = adapter or check["adapter"]
        rid = None
        if receipt:
            c = {"candidateSha256": "c" * 64, "programs": self.bound(adapter, part)}
            c.update(consumed or {})
            res = {"execution": status, "detector": detector, "findings": None, "coverage": [], "strength": "measured", "verdict": None}
            res.update(result or {})
            r = E.write_receipt(self.root, plan=self.plan, check={"checkId": cid}, arm={"armId": aid}, part=part, adapter=adapter,
                                argv=None, run_id=self.led.run_id, parent_run_id=None, started="2026-01-01T00:00:00Z",
                                ended="2026-01-01T00:00:05Z", exit_code=0, outputs=[self.root / o for o in outputs], log=None,
                                consumed=c, validation={"accepted": accepted, "rejections": rejections or []}, result=res, extra=extra)
            rid = r["receiptId"]
        self.led.transition(cid, aid, part, status, cause=cause, receipt_id=rid,
                            detector=detector if ledger_detector == "same" else ledger_detector,
                            rejections=rejections, extra=ledger_extra, detail=detail)


def emit(root: Path, rel: str, doc) -> str:
    write(root, rel, json.dumps(doc, indent=1))
    return rel


def diff_extra(folder, word, probe, *, new_defect=0, created=1, unmeasured=0, repaired=2) -> dict:
    block = {"orderEvaluation": {"classification": word, "probes": [probe],
                                 "diff": {"verdict": "DIFFERENTIAL", "counts": {"repaired": repaired, "reclassified": 1, "unmeasured": unmeasured}},
                                 "created": {"created": created, "newDefect": new_defect, "attributed": created - new_defect, "unmeasured": 0},
                                 "note": "kept verbatim"}}
    if folder:
        block["sweep"] = {"folder": folder, "job": f"diff-{probe}-x", "outcome": "ran", "reusedFrom": None}
    return block


def sweep_launch(root: Path, inv: str, stem: str, word, probes: dict) -> None:
    """One launch folder: per probe, the arm and reverse emits, its diff job record and its summary row."""
    jobs, rows = [], []
    for probe, spec in probes.items():
        arm = write(root, f"sweep/{inv}/{probe}/{stem}.json", spec.get("arm", f'{{"Probe": "{probe}", "launch": "{inv}"}}'))
        rev = write(root, f"sweep/{inv}/{probe}/{stem}.reverse.json", spec.get("reverse", f'{{"Probe": "{probe}", "reverse": true}}'))
        rec = {"job": f"diff-{probe}-{stem}", "kind": "diff", "outcome": spec.get("outcome", "ran"), "exitCode": spec.get("exit", 0),
               "emitWritten": spec.get("written", True), "inputs": {"arm": str(arm), "reverse": str(rev), "base": "ignored"}}
        if not spec.get("no_job"):
            jobs.append(json.dumps(rec))
        if not spec.get("no_row"):
            rows.append({"probe": probe, "diff": spec.get("diff"), "created": spec.get("created")})
    write(root, f"sweep/{inv}/jobs.jsonl", "\n".join(jobs + ["", "{broken", "[1]", '{"kind": "no job id"}']) + "\n")
    write(root, f"sweep/{inv}/summary.json", json.dumps({"candidates": {stem: {"classification": word, "rows": rows}}}))


def inputs_of(root: Path, inv: str, probe: str, stem: str) -> list:
    out = []
    for part, name in (("arm", f"{stem}.json"), ("reverse", f"{stem}.reverse.json")):
        rel = f"sweep/{inv}/{probe}/{name}"
        out.append({"part": part, "path": rel, "sha256": sha((root / rel).read_bytes())})
    return out


# ----------------------------------------------------------------------------------- order classification

def classification_cases(tmp: Path) -> dict:
    root = tmp / "classify"
    stem = "candidate-01-flip"
    arm = {"armId": "candidate-01", "candidateFile": stem + ".json"}
    d1 = {"verdict": "DIFFERENTIAL", "counts": {"repaired": 2, "reclassified": 1, "unmeasured": 0}}
    c1 = {"created": 1, "newDefect": 0, "attributed": 1, "unmeasured": 0}
    sweep_launch(root, "inv-0001", stem, "REPAIRS", {"overlapprobe": {"diff": d1, "created": c1}, "contactprobe": {"diff": d1, "created": c1}})
    sweep_launch(root, "inv-0002", stem, "UNRESOLVED", {
        "overlapprobe": {"diff": d1, "created": c1}, "contactprobe": {"diff": d1, "created": c1},
        "navownprobe": {"diff": d1, "created": c1, "exit": 3}, "waterprobe": {"diff": d1, "created": c1, "no_job": True},
        "seamprobe": {"diff": d1, "created": c1, "no_row": True}, "stateprobe": {"diff": d1, "created": c1, "written": False},
        "depprobe": {"diff": d1, "created": c1, "outcome": "reused"}})
    sweep_launch(root, "inv-0003", stem, None, {"overlapprobe": {"diff": d1, "created": c1}})
    sweep_launch(root, "inv-10000", stem, "LATE", {"overlapprobe": {"diff": d1, "created": c1}})
    sweep_launch(root, "inv-9999", stem, "EARLY", {"overlapprobe": {"diff": d1, "created": c1}})

    def row(inv, probe, word, *, folder="auto", diff=d1, created=c1, inputs="auto", probes=None):
        r = {"orderEvaluation": {"classification": word, "probes": probes if probes is not None else [probe], "diff": diff, "created": created}}
        f = f"sweep/{inv}" if folder == "auto" else folder
        if f is not None:
            r["sweep"] = {"folder": f}
        r["consumed"] = {"inputs": inputs_of(root, inv, probe, stem) if inputs == "auto" else inputs}
        return r

    cases = [
        ("no rows", []),
        ("rows without an order evaluation", [{"result": {}}, {"orderEvaluation": None}, {"orderEvaluation": {}}]),
        ("older layout, one word", [row("inv-0001", "overlapprobe", "REPAIRS", folder=None), row("inv-0001", "contactprobe", "REPAIRS", folder=None)]),
        ("older layout, two words (a tie)", [row("inv-0001", "overlapprobe", "REPAIRS", folder=None),
                                             row("inv-0001", "contactprobe", "UNRESOLVED", folder=None),
                                             row("inv-0001", "navownprobe", None, folder=None, inputs=[])]),
        ("a folder that is not a launch folder", [row("inv-0001", "overlapprobe", "A", folder="sweep/old"), row("inv-0001", "contactprobe", "B", folder="sweep")]),
        ("one launch, confirmed", [row("inv-0001", "overlapprobe", "REPAIRS"), row("inv-0001", "contactprobe", "REPAIRS")]),
        ("two launches, the newest measured both", [row("inv-0001", "overlapprobe", "REPAIRS"), row("inv-0002", "contactprobe", "UNRESOLVED",
                                                                                               inputs=inputs_of(root, "inv-0002", "contactprobe", stem)),
                                                    {"orderEvaluation": {"classification": "REPAIRS", "probes": ["overlapprobe"], "diff": d1, "created": c1},
                                                     "sweep": {"folder": "sweep/inv-0001"},
                                                     "consumed": {"inputs": inputs_of(root, "inv-0002", "overlapprobe", stem)}}]),
        ("the newest launch's emit differs", [row("inv-0001", "overlapprobe", "REPAIRS"),
                                              {"orderEvaluation": {"classification": "UNRESOLVED", "probes": ["contactprobe"], "diff": d1, "created": c1},
                                               "sweep": {"folder": "sweep/inv-0002"},
                                               "consumed": {"inputs": [{"part": "arm", "sha256": "0" * 64}, {"part": "reverse", "sha256": "1" * 64}]}}]),
        ("exit, no job, no row, not written, reused", [row("inv-0002", p, "UNRESOLVED") for p in
                                                       ("navownprobe", "waterprobe", "seamprobe", "stateprobe", "depprobe", "overlapprobe")]),
        ("rows differ", [row("inv-0002", "overlapprobe", "UNRESOLVED", diff={"verdict": "OTHER"}),
                         row("inv-0002", "contactprobe", "UNRESOLVED", created={"created": 9})]),
        ("no classification in the summary", [row("inv-0003", "overlapprobe", "X")]),
        ("numeric launch order", [row("inv-9999", "overlapprobe", "EARLY"), row("inv-10000", "overlapprobe", "LATE")]),
        ("equal numbers compare as numbers", [row("inv-0001", "overlapprobe", "REPAIRS", diff={"verdict": "DIFFERENTIAL", "counts": {"repaired": 2.0, "reclassified": True, "unmeasured": 0}})]),
        ("a launch folder that is gone", [row("inv-0001", "overlapprobe", "REPAIRS", folder="sweep/inv-0777")]),
        ("no probes listed", [row("inv-0001", "overlapprobe", "REPAIRS", probes=[])]),
    ]
    out = []
    for name, rows in cases:
        out.append({"name": name, "rows": mask(rows, root), "want": mask(E.order_classification(root, arm, copy.deepcopy(rows)), root)})
    jobs = [{"folder": inv, "want": [[k, v] for k, v in mask(E.read_sweep_jobs(root / "sweep" / inv), root).items()]}
            for inv in ("inv-0002", "inv-none")]
    folders = [{"name": n, "want": bool(E.SWEEP_FOLDER_RE.match(n))} for n in
               ("inv-0001", "inv-001", "inv-00001", "INV-0001", "inv-0001 ", "xinv-0001", "inv-12a4", "inv-١٢٣٤")]
    return {"tree": tree(root, masked=("jobs.jsonl",)), "arm": arm, "cases": out, "jobs": jobs, "folders": folders}


# ------------------------------------------------------------------------------------ the rich run folder

def rich_checks() -> list:
    return [
        check_row("l.masters", "scopeprobe", exe="scopeprobe", family="loadability"),
        check_row("g.overlap", "ordersweep-probe", exe="overlapprobe", prerequisites=["l.masters"],
                  arms=("baseline", "candidate-01", "supplied-01")),
        check_row("g.contact", "ordersweep-probe", exe="contactprobe", prerequisites=["l.masters"]),
        check_row("g.deps", "probe-json", exe="depprobe", result={"kind": "catalogue"}),
        check_row("p.discovery", "patchprobe", exe="patchprobe", family="patch", arms=("baseline",), result={"kind": "discovery"}),
        check_row("p.advice", "adviceprobe", exe="adviceprobe", family="patch", arms=("candidate-01",), required=False),
        check_row("p.qualification", "derived", family="patch", arms=("baseline",), required=False),
        check_row("p.order", "derived", family="patch"),
        check_row("r.contested", "mcp-only", family="records"),
        check_row("e.regen", "manual", family="environment", manualDecisionAllowed=True,
                  evidence={"regeneration": "regenerate LOD after the order changes"}),
        check_row("g.terrain", "unsupported", family="terrain", required=False),
        check_row("rt.media", "media", family="runtime", required=False, arms=("baseline",)),
        check_row("rt.live", "live-import", family="runtime", required=False, status="not-opted-in", applicability="unknown"),
        check_row("s.state", "probe-emit", exe="stateprobe", family="state"),
        check_row("u.unknown", "probe-json", exe="depprobe", applicability="unknown", applicabilityEvidence="the scope could not say", required=False),
        check_row("n.na", "probe-json", exe="depprobe", applicability="no", required=True),
        check_row("z.pending", "probe-json", exe="depprobe"),
    ]


def build_rich(root: Path, **plan_over) -> Builder:
    """A run folder holding one of nearly everything a disposition reports."""
    stem = "candidate-01-flip"
    arms = None
    plan = run_plan(root, rich_checks(), arms=arms, **plan_over)
    plan["arms"].append({"armId": "supplied-01", "kind": "supplied-combination", "candidateFile": "candidate-02-supplied.json",
                         "loadedInGameNote": "the supplied plugin is an ESP only", "payload": {"espOnly": True, "note": "its assets were not installed"},
                         "plugins": ["Skyrim.esm", "A.esp", "B.esp", "Target.esp", "Patch.esp"],
                         "supplied": {"declaredFiles": ["mods/P/Patch.esp", "Target.esp"]},
                         "candidatePath": str(root / "candidates" / "candidate-02-supplied.json")})
    plan["states"] = {"selected": ["baseline", "deep"], "depth": "deep", "notExplored": [{"state": "named", "why": "not selected"}],
                      "fromCensus": {"checks": ["s.state"], "sha256": "s" * 64}}
    plan["budgets"]["candidatesHeldBack"] = [{"file": "candidate-09.json", "why": "max candidates"}]
    plan["explicitDecisions"] = {"select": ["g.overlap", "l.masters"], "exclude": {"g.terrain": "it's unsupported"}, "note": "narrowed"}
    b = Builder(root, plan)
    d1 = {"verdict": "DIFFERENTIAL", "counts": {"repaired": 2, "reclassified": 1, "unmeasured": 0}}
    c1 = {"created": 1, "newDefect": 0, "attributed": 1, "unmeasured": 0}
    c2 = {"created": 3, "newDefect": 2, "attributed": 1, "unmeasured": 4}
    sweep_launch(root, "inv-0001", stem, "REPAIRS", {"overlapprobe": {"diff": d1, "created": c1}, "contactprobe": {"diff": d1, "created": c1}})
    sweep_launch(root, "inv-0002", "candidate-02-supplied", "REGRESSES", {"overlapprobe": {"diff": d1, "created": c2}})

    for aid in ("baseline", "candidate-01"):
        out = emit(root, f"outputs/l.masters/{aid}.json", {"complete": True, "arm": aid})
        b.settle("l.masters", aid, None, "completed", detector="no-finding", outputs=[out],
                 result={"findings": 0, "coverage": [{"Population": "plugins", "Judged": 4, "Unjudgeable": {}}]})
    # the swept checks: baseline arm, then the candidate's arm, reverse and diff
    for cid, probe in (("g.overlap", "overlapprobe"), ("g.contact", "contactprobe")):
        base = emit(root, f"sweep/inv-0001/{probe}/baseline.json", {"Probe": probe, "Conflicts": [
            {"A": "Target.esp", "B": "Other.esp"}, {"A": "third.esp", "B": "TARGET.ESP"}, {"A": "Target.esp", "B": "target.esp"}, {"A": "X.esp", "B": "Y.esp"}]})
        b.settle(cid, "baseline", "arm", "completed", detector="finding", outputs=[base],
                 result={"findings": 3, "verdict": "CONFLICTS", "coverage": [{"Population": "refs", "Judged": 10, "Unjudgeable": {"no-mesh": 2, "other": 0}}]})
        ins = inputs_of(root, "inv-0001", probe, stem)
        base_sha = sha((root / base).read_bytes())
        b.settle(cid, "candidate-01", "arm", "completed", detector="finding", outputs=[ins[0]["path"]],
                 consumed={"baseEmitPath": base, "baseEmitSha256": base_sha}, result={"findings": 1, "verdict": "CONFLICTS", "strength": None})
        b.settle(cid, "candidate-01", "reverse", "completed", detector="no-finding", outputs=[ins[1]["path"]],
                 consumed={"baseEmitPath": base, "baseEmitSha256": base_sha},
                 result={"findings": 0, "coverage": [{"population": "refs", "judged": 0, "unjudgeable": {"skipped": "3"}}, {"Population": "x", "Judged": 1}]})
        log = write(root, f"sweep/inv-0001/logs/diff-{probe}-{stem}.txt", f"$ conflictdiff\n(exit 0, 0.1s)\n\nDIFFERENTIAL {probe}\n")
        b.settle(cid, "candidate-01", "diff", "completed", detector="finding" if probe == "overlapprobe" else "no-finding",
                 outputs=[log.relative_to(root).as_posix()], consumed={"inputs": ins},
                 result={"findings": 1, "verdict": "DIFFERENTIAL", "strength": "differential"},
                 extra=diff_extra("sweep/inv-0001", "REPAIRS", probe))
    sins = inputs_of(root, "inv-0002", "overlapprobe", "candidate-02-supplied")
    b.settle("g.overlap", "supplied-01", "arm", "completed", detector="no-finding", outputs=[sins[0]["path"]], result={"findings": 0})
    b.settle("g.overlap", "supplied-01", "reverse", "completed", detector="no-finding", outputs=[sins[1]["path"]], result={"findings": 0})
    b.settle("g.overlap", "supplied-01", "diff", "completed", detector="finding", consumed={"inputs": sins},
             result={"findings": 2, "verdict": "DIFFERENTIAL-REGRESSION"},
             extra=diff_extra("sweep/inv-0002", "REGRESSES", "overlapprobe", new_defect=2, created=3, unmeasured=4))
    # catalogues
    for aid in ("baseline", "candidate-01"):
        out = emit(root, f"outputs/g.deps/{aid}.json", {"rows": [1, 2, 3]})
        b.settle("g.deps", aid, None, "completed" if aid == "baseline" else "reused", detector="catalogue", outputs=[out],
                 result={"coverage": [{"Population": "dependencies", "Judged": 3}]} if aid == "baseline" else {"findings": 5, "coverage": []},
                 ledger_extra={"reusedFromRunId": "evalrun-checks-0"} if aid != "baseline" else None)
    hits = emit(root, "outputs/p.discovery/baseline.json", {"schema": 1, "hits": [
        {"file": "A-B patch.esp", "masters": ["a.ESP", "B.esp"]}, {"file": "Unrelated.esp", "masters": ["Skyrim.esm"]},
        {"file": "Fomod option", "detail": {"conditions": ["target.esp"]}}, {"file": "Pair hit", "detail": {"pair": ["PATCH.esp", "Other.esp"]}},
        {"file": "", "masters": ["Target.esp"]}, "not a hit", {"file": "Other-only.esp", "masters": ["Other.esp"]}]})
    b.settle("p.discovery", "baseline", None, "completed", detector="catalogue", outputs=[hits], result={"findings": 6, "coverage": [{"Judged": 6}]})
    report = emit(root, "outputs/p.advice/report-1234.json", {"rules": [{"r": 1}, {"r": 2}], "clashes": [
        {"namedModId": 77, "namedModName": "Other Mod", "pageModId": 1234, "page": "description", "readDate": "2026-10-01", "line": "conflicts with Other Mod",
         "symptom": None, "extra": "dropped"}, "not a clash", {"namedModId": 78}]})
    b.settle("p.advice", "candidate-01", None, "completed", detector="catalogue", outputs=[report],
             consumed={"configuration": {"source": "catalogue-snapshot:2026-10-01"}}, result={"findings": 2})
    b.settle("p.qualification", "baseline", None, "completed", detector="catalogue", result={"findings": 1, "coverage": [{"Population": "candidates", "Judged": 1}]},
             extra={"derived": {"candidates": [{"file": "A-B patch.esp", "qualified": False}], "notSearched": ["a pool nobody configured"],
                                "notRelevant": {"count": 2, "why": "names no target"}}, "derivedFrom": []})
    # the derived order evaluation: follows the diffs it consumed
    diffs = [E.load_json(root / "receipts" / f"{c}__candidate-01__diff.json")["receiptId"] for c in ("g.overlap", "g.contact")]
    b.settle("p.order", "candidate-01", None, "completed", detector="no-finding", result={"findings": 0}, extra={"derivedFrom": diffs},
             ledger_extra={"derivedFrom": diffs})
    b.settle("p.order", "baseline", None, "not-applicable", detail="the baseline has no order evaluation of itself")
    # imports: fresh on the baseline, reused on the candidate with the identity limit rule (f) writes
    for aid, imp, limits in (
            ("baseline", {"origin": "fresh", "capturedUtc": "2026-01-02T00:00:00Z"}, []),
            ("candidate-01", {"origin": "reused", "capturedUtc": "2026-01-02T00:00:01Z", "reusedFromVerified": 1, "reusedWithin": "this-evaluation",
                              "reusedFrom": {"planId": "plan00000001", "armId": "baseline", "resultSha256": "d" * 64}},
             [{"plugin": "x"}, {"armCandidate": "candidate-01-flip.json", "capturedAgainst": "candidate-00-baseline.json"}])):
        res = write(root, f"imports/r.contested__{aid}/result.json", json.dumps({"schemaVersion": 1, "arm": aid}))
        b.settle("r.contested", aid, None, "completed", detector="no-finding", outputs=[res.relative_to(root).as_posix()],
                 result={"findings": 0, "coverage": [{"Population": "records", "Judged": 9}]},
                 extra={"imported": imp, "validation": {"accepted": True, "rejections": [], "identityLimits": limits}},
                 ledger_extra={"imported": True, "importOrigin": imp["origin"]})
    for aid in ("baseline", "candidate-01"):
        b.settle("e.regen", aid, None, "manual", detail="a person decides this one")
        b.settle("g.terrain", aid, None, "unsupported")
    b.settle("rt.media", "baseline", None, "blocked(no-identified-media)", cause=None)
    # deep states: the generated not-explored rows, one arm's check not applicable, the other with unjudged rows
    write(root, "outputs/s.state/baseline.states.json", json.dumps({"states": [], "notExplored": [{"switch": "0001:A.esp", "why": "no flip"}]}))
    write(root, "outputs/s.state/candidate-01.states.json", json.dumps({"states": ["s1"], "notExplored": [
        {"switch": "0002:B.esp", "why": "budget", "armId": "overridden"}]}))
    b.settle("s.state", "baseline", None, "not-applicable(no switch)", detail="the census found no switch")
    out = emit(root, "outputs/s.state/candidate-01.json", {"Probe": "stateprobe"})
    b.settle("s.state", "candidate-01", None, "completed", detector=None, outputs=[out],
             result={"detectorUndetermined": True, "coverage": [{"Population": "state rows", "Judged": 2, "Unjudgeable": {"primitive": 5}}]})
    for aid in ("baseline", "candidate-01"):
        out = emit(root, f"outputs/u.unknown/{aid}.json", {"rows": []})
        b.settle("u.unknown", aid, None, "completed", detector="no-finding", outputs=[out], result={"findings": 0})
    b.settle("n.na", "baseline", None, "not-applicable")
    b.settle("n.na", "candidate-01", None, "rejected(exit-nonzero)", cause="exit-nonzero", accepted=False,
             rejections=[{"cause": "exit-nonzero", "detail": "exit 3"}])
    # decisions: one accepted, the refused kinds, one unreadable file
    note = write(root, "decisions/evidence/regen-note.txt", "LOD regenerated on 2026-01-03\n")
    ok = {"checkId": "e.regen", "armId": "baseline", "decidedBy": "Reviewer", "decidedUtc": "2026-01-03T00:00:00Z",
          "decision": "completed-no-finding", "note": "regenerated", "evidence": ["decisions/evidence/regen-note.txt"]}
    for name, doc in (("01-accepted.json", ok), ("02-no-check.json", dict(ok, checkId="no.such")),
                      ("03-not-allowed.json", dict(ok, checkId="g.deps")), ("04-bad-word.json", dict(ok, decision="fine")),
                      ("05-no-author.json", dict(ok, decidedBy="")), ("06-no-evidence.json", dict(ok, armId="candidate-01", evidence=["gone.txt", "also-gone.txt"])),
                      ("07-mcp-only.json", dict(ok, checkId="r.contested")), ("08-null-id.json", dict(ok, checkId=None)),
                      ("09-no-evidence-key.json", {k: v for k, v in ok.items() if k != "evidence"} | {"armId": "candidate-01", "decision": "not-applicable"})):
        write(root, "decisions/" + name, json.dumps(doc))
    b.led.open_invocation("resume", "evalrun-checks-2", "evalpipeline-1", budget_minutes=30)
    b.led.open_invocation("resume", "evalrun-checks-3", None, budget_minutes=12.5)
    return b


def judge(root: Path) -> dict:
    """What finalize derives from a run folder: integrity, the bad keys, the disposition's bytes and STATUS.md."""
    plan = E.load_json(root / "plan.json")
    with fake_clock():
        ledger = E.Ledger(root, plan)
        receipts = E.read_receipts(root)
        integrity, bad = E.evidence_integrity(root, plan, ledger, receipts)
        disp = E.build_disposition(root, plan, ledger, receipts, integrity=integrity, bad_keys=bad)
        status = E.status_md(plan, disp)
    return {"integrity": json.dumps(integrity, indent=1, ensure_ascii=False), "bad": [[k, v] for k, v in bad.items()],
            "disposition": json.dumps(disp, indent=1, ensure_ascii=False), "status": status}


def packed(d: dict, *keys) -> dict:
    return {k: (pack(v) if k in keys else v) for k, v in d.items()}


def json_diff(old, new, path: list, edits: list) -> None:
    """The set, pop and append steps that turn old into new, a new key landing last as dict.update puts it."""
    if isinstance(old, dict) and isinstance(new, dict):
        for k in old:
            if k not in new:
                edits.append(["pop", path + [k]])
        for k in new:
            if k not in old:
                edits.append(["set", path + [k], new[k]])
            else:
                json_diff(old[k], new[k], path + [k], edits)
    elif isinstance(old, list) and isinstance(new, list) and len(new) >= len(old):
        for i, (a, b) in enumerate(zip(old, new)):
            json_diff(a, b, path + [i], edits)
        for x in new[len(old):]:
            edits.append(["append", path, x])
    elif type(old) is not type(new) or old != new:
        edits.append(["set", path, new])


def json_apply(doc, edits: list) -> None:
    for kind, path, *value in edits:
        at = doc
        for step in path[:-1] if kind != "append" else path:
            at = at[step]
        if kind == "append":
            at.append(copy.deepcopy(value[0]))
        elif kind == "pop":
            del at[path[-1]]
        else:
            at[path[-1]] = copy.deepcopy(value[0])


class Variant:
    """A copy of a base folder with recorded changes, so the C# side can make the same ones."""

    def __init__(self, tmp: Path, base: Path, name: str):
        self.root = tmp / "variants" / re.sub(r"[^A-Za-z0-9]+", "-", name) / "b"
        shutil.copytree(base, self.root)
        self.base, self.ops = base, []
        # the copy's plan and sweep job records name the copy's own files, as the folder rebuilt in C# does
        was, now = (str(r).replace("\\", "\\\\") for r in (base, self.root))
        for p in self.root.rglob("*"):
            if p.name in ("plan.json", "jobs.jsonl") and was in p.read_bytes().decode("utf-8"):
                p.write_bytes(p.read_bytes().decode("utf-8").replace(was, now).encode("utf-8"))
                if p.name == "plan.json":
                    write(self.root, "plan.sha256", sha(p.read_bytes()) + "\n")

    def delete(self, rel):
        (self.root / rel).unlink()
        self.ops.append({"op": "delete", "path": rel})

    def rmtree(self, rel):
        shutil.rmtree(self.root / rel)
        self.ops.append({"op": "rmtree", "path": rel})

    def put(self, rel, text):
        write(self.root, rel, text)
        self.ops.append({"op": "write", "path": rel, "text": pack(mask(mask(text, self.root), self.base))})

    def edit(self, rel, fn):
        """Change a JSON file; recorded as the edits that make the same change, not as the whole new file."""
        old = json.loads((self.root / rel).read_text(encoding="utf-8"))
        new = copy.deepcopy(old)
        fn(new)
        edits = []
        json_diff(old, new, [], edits)
        again = copy.deepcopy(old)
        json_apply(again, edits)
        assert jtext(again) == jtext(new), f"the recorded edits of {rel} do not rebuild it"
        write(self.root, rel, jtext(new))
        self.ops.append({"op": "json", "path": rel, "edits": mask(mask(edits, self.root), self.base)})
        if rel == "plan.json":
            self.resha()

    def resha(self):
        write(self.root, "plan.sha256", sha((self.root / "plan.json").read_bytes()) + "\n")
        self.ops.append({"op": "planSha"})


def run_tree(root: Path) -> list:
    """A run folder's tree: plan.json and the sweep job records as text with the root masked, plan.sha256 recomputed."""
    rows = [r for r in tree(root, masked=("plan.json", "jobs.jsonl")) if r["path"] != "plan.sha256"]
    return rows + [{"path": "plan.sha256", "planSha": True}]


def disposition_cases(tmp: Path) -> dict:
    bases, cases = {}, []

    def base(name, build):
        root = tmp / "bases" / name
        root.mkdir(parents=True)
        with fake_clock():
            build(root)
        bases[name] = (root, run_tree(root))
        return root

    def case(name, base_name, change=None):
        v = Variant(tmp, bases[base_name][0], name)
        if change:
            change(v)
        # masked through the BASE root too: a variant is a copy, and its plan still names the base folder's premises
        cases.append({"name": name, "base": base_name, "ops": v.ops,
                      **packed(mask(mask(judge(v.root), v.root), bases[base_name][0]), "integrity", "disposition", "status")})

    def simple(root, findings):
        b = Builder(root, run_plan(root, [check_row("l.masters", "scopeprobe", exe="scopeprobe", family="loadability"),
                                          check_row("g.deps", "probe-json", exe="depprobe")]))
        for cid in ("l.masters", "g.deps"):
            for aid in ("baseline", "candidate-01"):
                out = emit(root, f"outputs/{cid}/{aid}.json", {"ok": True})
                found = findings and cid == "g.deps" and aid == "candidate-01"
                b.settle(cid, aid, None, "completed", detector="finding" if found else "no-finding", outputs=[out],
                         result={"findings": 4 if found else 0, "verdict": "DEFECTS" if found else None, "coverage": [{"Population": "p", "Judged": 1}]})

    base("complete", lambda r: simple(r, False))
    base("findings", lambda r: simple(r, True))
    base("rich", build_rich)
    base("legacy", lambda r: build_rich(r, toolIdentity=None, reviewArm="baseline"))

    case("complete, no findings", "complete")
    case("complete, with findings", "findings")
    case("rich", "rich")
    case("legacy plan with a review arm", "legacy")

    # missing required rows, each cause
    def causes(v):
        def ledger(doc):
            e = doc["entries"]
            e["l.masters__baseline"].update(status="rejected(exit-nonzero)", cause="exit-nonzero", detector=None)
            e["l.masters__candidate-01"].update(status="skipped-budget", cause="budget-exhausted", detector=None)
            e["g.deps__baseline"].update(detector=None)
            e["g.deps__candidate-01"].update(status="failed", cause=None)
        v.edit("ledger.json", ledger)
    case("missing rows: rejected, skipped, undetermined, failed", "complete", causes)

    def more_causes(v):
        def ledger(doc):
            e = doc["entries"]
            e["l.masters__baseline"].update(status="blocked(prerequisite:x.y)", cause="blocked-prerequisite")
            e["l.masters__candidate-01"].update(status="interrupted", cause="the process did not write a terminal transition")
            del e["g.deps__baseline"]
            e["g.deps__candidate-01"].update(status=None, cause="")
        v.edit("ledger.json", ledger)
    case("missing rows: blocked, interrupted, pending, no status", "complete", more_causes)

    def unknown(v):
        def plan(doc):
            doc["checks"][1].update(applicability="unknown", applicabilityEvidence=None)
            doc["checks"][0].update(applicability="unknown", applicabilityEvidence="the scope is incomplete", required=False)
        v.edit("plan.json", plan)
    case("unknown applicability", "complete", unknown)

    def unknown_without_arms(v):
        v.edit("plan.json", lambda doc: doc["checks"].append(check_row("x.none", "probe-json", exe="depprobe", applicability="unknown",
                                                                      arms=(), required=False)))
    case("unknown applicability on a check with no arm", "complete", unknown_without_arms)
    case("a journal with blank lines and a line separator", "complete", lambda v: v.put(
        "journal.jsonl", (v.root / "journal.jsonl").read_bytes().decode("utf-8") + "\r\n   \r\n{\"detail\":\"a\u2028b\"}\r\n\n"))

    def decided(v):
        def plan(doc):
            doc["checks"][1]["manualDecisionAllowed"] = True
        v.edit("plan.json", plan)
        v.edit("ledger.json", lambda doc: doc["entries"]["g.deps__candidate-01"].update(status="rejected(refused)", cause="refused", detector=None))
        v.put("decisions/a.json", json.dumps({"checkId": "g.deps", "armId": "candidate-01", "decidedBy": "P", "decidedUtc": "t",
                                              "decision": "completed-with-finding", "note": None, "evidence": []}))
    case("an accepted manual decision completes a row", "complete", decided)

    def budgets_only(v):
        v.edit("ledger.json", lambda doc: doc["runIds"].append({"runId": "r9", "parentRunId": None, "command": "resume", "startedUtc": "t", "budgetMinutes": 0}))
        v.edit("plan.json", lambda doc: doc["budgets"].pop("minutes"))
    case("a zero budget in force and no plan minutes", "findings", budgets_only)

    # evidence integrity, each defect kind
    def integrity_case(name, change):
        case("integrity: " + name, "rich", change)

    integrity_case("a receipt that cannot be read", lambda v: v.put("receipts/l.masters__baseline.json", "{oops"))
    integrity_case("a receipt with another id", lambda v: v.edit("receipts/l.masters__baseline.json", lambda d: d.update(receiptId="another")))
    integrity_case("a receipt not accepted", lambda v: v.edit("receipts/g.deps__baseline.json", lambda d: d["validation"].update(accepted=False)))
    integrity_case("an emitter with no digest", lambda v: v.edit("receipts/g.deps__baseline.json", lambda d: d["consumed"]["programs"]["emitter"].pop("digest")))
    integrity_case("a controller of another build", lambda v: v.edit("receipts/g.deps__baseline.json",
                                                                    lambda d: d["consumed"]["programs"]["controller"].update(digest="scripts:" + "e" * 64)))
    integrity_case("an output that is gone", lambda v: v.delete("outputs/g.deps/baseline.json"))
    integrity_case("an output with no recorded hash", lambda v: v.edit("receipts/g.deps__baseline.json", lambda d: d["outputs"][0].update(sha256=None)))
    integrity_case("an output that changed", lambda v: v.put("outputs/l.masters/baseline.json", "changed"))
    integrity_case("a baseline emit that changed spreads", lambda v: v.put("sweep/inv-0001/overlapprobe/baseline.json", "changed"))
    integrity_case("a reverse emit that is gone", lambda v: v.delete("sweep/inv-0001/contactprobe/candidate-01-flip.reverse.json"))
    integrity_case("a comparison input with another hash", lambda v: v.edit("receipts/g.overlap__supplied-01__diff.json",
                                                                           lambda d: d["consumed"]["inputs"][1].update(sha256="0" * 64, part=None)))
    integrity_case("an import result that changed", lambda v: v.put("imports/r.contested__candidate-01/result.json", "{}"))
    integrity_case("a discovery hit list that is not a list", lambda v: (
        v.put("outputs/p.discovery/baseline.json", '{"hits": {}}'),
        v.edit("receipts/p.discovery__baseline.json", lambda d: d["outputs"][0].update(
            sha256=sha(b'{"hits": {}}')))))
    integrity_case("an advice report that cannot be read", lambda v: (
        v.put("outputs/p.advice/report-1234.json", "[]"),
        v.edit("receipts/p.advice__candidate-01.json", lambda d: d["outputs"][0].update(sha256=sha(b"[]")))))
    def imports(v):
        v.edit("receipts/r.contested__baseline.json", lambda d: d["imported"].update(origin="older"))
        def reused(d):
            d["imported"].pop("reusedWithin")
            d["imported"]["reusedFrom"] = None
            d["validation"]["identityLimits"] = [{"armCandidate": "candidate-01-flip.json", "capturedAgainst": None}]
        v.edit("receipts/r.contested__candidate-01.json", reused)
    case("imports: an unknown origin and an unstated reuse", "rich", imports)
    case("imports: reuse from another evaluation, no limit", "rich", lambda v: v.edit(
        "receipts/r.contested__candidate-01.json",
        lambda d: (d["imported"].update(reusedWithin="another-evaluation", reusedFromVerified=0), d["validation"].update(identityLimits=None))))
    return {"bases": {k: t for k, (_, t) in bases.items()}, "cases": cases}


# ------------------------------------------------------------------------------------------ the plan's pairs

def pair_cases(tmp: Path) -> dict:
    """target_pairs per arm, and the review pairs, all pairs and relevant plugins of the review-arm run folder."""
    arms = [{"armId": "b", "kind": "baseline", "plugins": ["Skyrim.esm", "b.esp", "A.esp", "Z.esp", "a.esp", "Target.esp"]},
            {"armId": "c1", "kind": "candidate", "plugins": ["Skyrim.esm", "A.esp", "B.esp", "Target.esp", "New.esp", "Z.esp"]},
            {"armId": "c2", "kind": "candidate", "plugins": ["z.esp", "target.esp", "skyrim.esm"]},
            {"armId": "s1", "kind": "supplied-combination", "plugins": ["Skyrim.esm"],
             "supplied": {"declaredFiles": ["mods/P/patch.esp", "TARGET.esp", "mods\\Q\\Other Patch.esp"]}},
            {"armId": "s2", "kind": "supplied-combination", "supplied": None}, {"armId": "s3", "kind": "candidate", "plugins": None}]
    plans = {"targets and a baseline": {"arms": arms, "targets": [{"name": "Target.esp"}, {"name": "alpha.esp"}, {"name": ""}, "odd", {"x": 1}]},
             "no baseline": {"arms": arms[1:], "targets": [{"name": "Target.esp"}]},
             "no targets": {"arms": arms}}
    target = []
    for name, plan in plans.items():
        for arm in plan["arms"]:
            target.append({"plan": name, "arm": arm["armId"],
                           "want": E.target_pairs(SimpleNamespace(plan=plan, run=tmp), arm)})
    run = tmp / "bases" / "legacy"
    plan = E.load_json(run / "plan.json")
    ex = SimpleNamespace(plan=plan, run=run)
    return {"plans": plans, "target": target, "review": E.review_pairs(ex), "all": E.all_plan_pairs(ex),
            "relevant": sorted(E.relevant_plugins(ex))}


# ------------------------------------------------------------------------------------- the finalize command

def finalize_cases(tmp: Path, bases: dict) -> list:
    out = []

    def case(name, base_name, change=None):
        v = Variant(tmp, tmp / "bases" / base_name, "cmd " + name)
        if change:
            change(v)
        for n in ("disposition.json", "STATUS.md"):
            (v.root / n).unlink(missing_ok=True)
        with fake_clock(), captured() as (o, e), cwd(v.root):
            code = E.cmd_finalize(SimpleNamespace(run=str(v.root)))
        files = {n: (pack(mask((v.root / n).read_bytes().decode("utf-8"), v.root)) if (v.root / n).exists() else None)
                 for n in ("disposition.json", "STATUS.md")}
        out.append({"name": name, "base": base_name, "ops": v.ops, "exit": code, "stdout": mask(o.getvalue(), v.root),
                    "stderr": mask(e.getvalue(), v.root), "files": files})

    case("complete", "complete")
    case("findings", "findings")
    case("rich", "rich")
    case("integrity refused, one entry", "rich", lambda v: v.put("outputs/l.masters/baseline.json", "changed"))
    case("integrity refused, many entries", "rich", lambda v: v.put("sweep/inv-0001/overlapprobe/baseline.json", "changed"))
    case("no plan", "complete", lambda v: v.delete("plan.json"))
    case("no plan hash", "complete", lambda v: v.delete("plan.sha256"))
    case("plan changed", "complete", lambda v: v.put("plan.json", "{}"))
    case("plan schema 1", "complete", lambda v: v.edit("plan.json", lambda d: d.update(schemaVersion=1)))
    case("ledger gone", "complete", lambda v: v.delete("ledger.json"))
    case("ledger of another plan", "complete", lambda v: v.edit("ledger.json", lambda d: d.update(planId="other")))
    case("registry changed", "complete", lambda v: v.put("premises/registry.json", "changed"))
    case("registry gone", "complete", lambda v: v.delete("premises/registry.json"))
    case("scope changed", "complete", lambda v: v.put("premises/scope.json", "changed"))
    case("scope gone", "complete", lambda v: v.delete("premises/scope.json"))
    case("a relative registry path", "complete", lambda v: v.edit("plan.json", lambda d: d["registry"].update(path="nowhere/registry.json")))
    case("a settled entry's receipt is gone", "complete", lambda v: v.delete("receipts/g.deps__baseline.json"))
    case("a rejected entry's receipt is gone", "rich", lambda v: v.delete("receipts/n.na__candidate-01.json"))
    case("a running entry needs no receipt", "complete", lambda v: (
        v.edit("ledger.json", lambda d: d["entries"]["g.deps__baseline"].update(status="running")), v.delete("receipts/g.deps__baseline.json")))
    case("a receipt of another plan", "complete", lambda v: v.edit("receipts/g.deps__baseline.json", lambda d: d.update(planId="foreign")))
    case("no journal and no receipts folder", "complete", lambda v: (
        v.delete("journal.jsonl"), v.rmtree("receipts"), v.edit("ledger.json", lambda d: d.update(entries={}))))
    return out


# -------------------------------------------------------------------------------------------- verify-bundle

def make_bundle(run: Path, out: Path) -> None:
    """A bundle laid out as evalrun's bundle verb lays one out, from a finalized synthetic run folder: control files,
    receipts, decisions, receipt outputs and inputs under evidence/, the premise copies and a manifest."""
    plan = E.load_json(run / "plan.json")
    evidence, required = [], []

    def copy_in(src: Path, rel: str, kind: str, recorded=None, **more):
        if any(r["path"] == rel for r in evidence) or not src.is_file():
            return
        dst = out / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, dst)
        evidence.append({"id": E.sha256_text(rel)[:16], "kind": kind, "path": rel, "sha256": recorded or E.sha256_file(src),
                         "bytes": dst.stat().st_size, **more})

    stage = out.parent / (out.name + "-stage")
    neutral = (run / "plan.json").read_bytes().decode("utf-8").replace(str(run).replace("\\", "\\\\"), "C:/fixture-run")
    write(stage, "plan.json", neutral)
    write(stage, "plan.sha256", sha(neutral.encode("utf-8")) + "\n")
    for name in E.CONTROL_FILES:
        copy_in((stage if name.startswith("plan.") else run) / name, name, "control")
        required.append(name)
    for sub in ("receipts", "decisions"):
        for p in sorted((run / sub).glob("**/*")):
            copy_in(p, f"{sub}/{p.relative_to(run / sub).as_posix()}", sub.rstrip("s"))
    ledger = E.load_json(run / "ledger.json")
    for k, rec in E.read_receipts(run).items():
        ok = (ledger["entries"].get(k) or {}).get("status") in E.TERMINAL_OK
        for o in rec.get("outputs") or []:
            copy_in(run / o["path"], E.receipt_evidence_rel(o["path"]), "emit", recorded=o.get("sha256"), checkId=rec["checkId"], armId=rec["armId"])
            if ok:
                required.append(E.receipt_evidence_rel(o["path"]))
        c = rec.get("consumed") or {}
        for path in [c.get("baseEmitPath")] + [x.get("path") for x in c.get("inputs") or []]:
            if path:
                copy_in(run / path, E.receipt_evidence_rel(path), "emit")
        if ok:
            required.append(f"receipts/{k}.json")
    for d in E.load_decisions(run, plan):
        for row in d.get("evidence") or []:
            if d.get("accepted"):
                copy_in(run / row["path"], f"evidence/{row['path']}", "decision-evidence")
    copy_in(Path(plan["registry"]["path"]), "registry/check-registry.json", "registry")
    copy_in(Path(plan["scope"]["path"]), "scope/scope.json", "scope")
    required += ["registry/check-registry.json", "scope/scope.json"]
    manifest = {"bundleSchema": 2, "planId": plan["planId"], "createdUtc": "2026-01-05T00:00:00Z", "runIds": ledger["runIds"],
                "required": required, "evidence": evidence, "plan": "plan.json", "ledger": "ledger.json", "journal": "journal.jsonl",
                "disposition": "disposition.json", "status": "STATUS.md", "registry": "registry/check-registry.json",
                "search": None, "searchAbsent": "the plan records no candidate search", "scope": "scope/scope.json",
                "enrichmentStatus": {"names": {"status": "skipped", "sentence": "names were not asked for"}}}
    write(out, "manifest.json", jtext(manifest))


def add_enrichment(v: "Variant", rel: str, doc, **row) -> None:
    """One enrichment file and its manifest row."""
    text = doc if isinstance(doc, str) else json.dumps(doc, indent=1)
    v.put(rel, text)
    v.edit("manifest.json", lambda m: m["evidence"].append(
        {"id": E.sha256_text(rel)[:16], "kind": "enrichment", "path": rel, "sha256": sha(text.encode("utf-8")), "bytes": len(text.encode("utf-8")), **row}))


def verify_cases(tmp: Path) -> dict:
    run = tmp / "bundle-run"
    with fake_clock():
        build_rich(run, search={"path": None, "sha256": None, "absent": "no search"})
        with captured():
            code = E.cmd_finalize(SimpleNamespace(run=str(run)))
    assert code == 0, code
    base = tmp / "bundle-base"
    make_bundle(run, base)
    cases = []

    def case(name, change=None, out_file=False):
        v = Variant(tmp, base, "verify " + name)
        if change:
            change(v)
        target = v.root.parent / "verify-out.json"
        with fake_clock(), captured() as (o, e):
            code = E.cmd_verify_bundle(SimpleNamespace(dir=str(v.root), out=str(target) if out_file else None))
        where = target if out_file else v.root / "verify.json"
        cases.append({"name": name, "ops": v.ops, "out": out_file, "exit": code, "stdout": mask(o.getvalue(), v.root),
                      "stderr": mask(e.getvalue(), v.root), "verify": pack(mask(where.read_bytes().decode("utf-8"), v.root)),
                      "wroteInside": (v.root / "verify.json").exists()})

    def manifest(fn):
        return lambda v: v.edit("manifest.json", fn)

    def drop_row(m, path):
        m["evidence"] = [r for r in m["evidence"] if r["path"] != path]

    case("intact")
    case("intact, with --out", out_file=True)
    case("no manifest", lambda v: v.delete("manifest.json"))
    case("manifest not json", lambda v: v.put("manifest.json", "[1]"))
    case("schema 1", manifest(lambda m: m.update(bundleSchema=1)))
    case("schema absent and keys missing", manifest(lambda m: [m.pop(k) for k in ("bundleSchema", "journal", "status")]))
    case("manifest keys missing", manifest(lambda m: [m.pop(k) for k in ("required", "registry")]))
    case("a required file deleted", lambda v: v.delete("STATUS.md"))
    case("an evidence byte changed", lambda v: v.put("evidence/outputs/l.masters/baseline.json", "changed"))
    case("an unreferenced evidence file deleted", lambda v: v.delete("decisions/02-no-check.json"))
    case("plan changed", lambda v: v.put("plan.json", jtext(dict(E.load_json(base / "plan.json"), note="edited"))))
    case("plan hash file gone", lambda v: v.delete("plan.sha256"))
    case("plan gone", lambda v: v.delete("plan.json"))
    case("planId differs in the ledger", lambda v: v.edit("ledger.json", lambda d: d.update(planId="other")))
    case("planId differs in the manifest and disposition", lambda v: (manifest(lambda m: m.update(planId=None))(v),
                                                                     v.edit("disposition.json", lambda d: d.update(planId=7))))
    case("registry copy gone", lambda v: v.delete("registry/check-registry.json"))
    case("registry copy changed", lambda v: v.put("registry/check-registry.json", "{}"))
    case("scope copy changed", lambda v: v.put("scope/scope.json", "{}"))
    case("scope named elsewhere and absent", manifest(lambda m: m.update(scope="scope/other.json")))
    case("a search hash with no copy", lambda v: v.edit("plan.json", lambda d: d.update(search={"sha256": "5" * 64})))
    case("a receipt removed", lambda v: v.delete("receipts/g.deps__baseline.json"))
    case("a receipt with another id", lambda v: v.edit("receipts/g.deps__baseline.json", lambda d: d.update(receiptId="another")))
    case("a receipt not accepted", lambda v: v.edit("receipts/g.deps__baseline.json", lambda d: d["validation"].update(accepted=False)))
    case("a receipt's bound digest changed", lambda v: v.edit("receipts/g.deps__baseline.json", lambda d: (
        d["consumed"]["programs"]["emitter"].update(digest="folder:" + "9" * 64), d["consumed"]["programs"].pop("controller"))))
    case("an output omitted", lambda v: v.delete("evidence/outputs/g.deps/baseline.json"))
    case("an output with no recorded hash, omitted", lambda v: (
        v.edit("receipts/g.deps__baseline.json", lambda d: d["outputs"][0].update(sha256=None)), v.delete("evidence/outputs/g.deps/baseline.json")))
    case("a base emit changed", lambda v: v.put("evidence/sweep/inv-0001/overlapprobe/baseline.json", "changed"))
    case("a comparison input omitted", lambda v: v.delete("evidence/sweep/inv-0001/contactprobe/candidate-01-flip.reverse.json"))
    case("a manifest row dropped", manifest(lambda m: drop_row(m, "evidence/outputs/g.deps/baseline.json")))
    case("decision evidence omitted", lambda v: v.delete("evidence/decisions/evidence/regen-note.txt"))
    case("the disposition claims a finding the ledger lacks", lambda v: v.edit("disposition.json", lambda d: (
        d["evaluation"]["findings"].append({"checkId": "g.deps", "armId": "baseline", "part": None}),
        d["evaluation"]["completedNullChecks"].append({"checkId": "no.such", "armId": "x", "part": "arm"}),
        d["evaluation"]["completedCatalogues"].append({"checkId": "n.na", "armId": "candidate-01"}))))
    case("complete with missing rows", lambda v: v.edit("disposition.json", lambda d: d["evaluation"].update(complete=True)))
    case("a stale derivedFrom", lambda v: v.edit("disposition.json", lambda d: d["derivedFrom"].update(journalLines=1)))
    case("no derivedFrom", lambda v: v.edit("disposition.json", lambda d: d.pop("derivedFrom")))
    case("a journal line added", lambda v: v.put("journal.jsonl", (base / "journal.jsonl").read_bytes().decode("utf-8") + "{}\r\n"))
    case("the ledger edited", lambda v: v.edit("ledger.json", lambda d: d["entries"]["g.terrain__baseline"].update(detail="edited")))
    case("no disposition", lambda v: v.delete("disposition.json"))
    case("no ledger", lambda v: v.delete("ledger.json"))
    case("a legacy plan", lambda v: (v.edit("plan.json", lambda d: d.pop("toolIdentity")), ))

    # enrichment
    disp_sha = sha((base / "disposition.json").read_bytes())
    plugin_sha = "ab" * 32
    src = [{"path": "evidence/outputs/l.masters/baseline.json", "sha256": sha((base / "evidence/outputs/l.masters/baseline.json").read_bytes())}]

    def with_plugins(v):
        v.edit("plan.json", lambda d: d["arms"][0].update(requested={"plugins": [{"name": "A.esp", "sha256": plugin_sha.upper()}, {"name": "B.esp"}]}))

    case("enrichment: a record beside this disposition", lambda v: add_enrichment(
        v, "enrichment/record.json", {"dispositionSha256": disp_sha}, role="record"))
    case("enrichment: a record beside another disposition", lambda v: (
        add_enrichment(v, "enrichment/record.json", {"dispositionSha256": "0" * 64}, role="record"),
        add_enrichment(v, "enrichment/record2.json", "[]", role="record"),
        add_enrichment(v, "enrichment/capture.jsonl", "anything", role="capture", enrichment="names")))
    case("enrichment: record rows citing their sources", lambda v: add_enrichment(
        v, "enrichment/rows.json", {"derivedFrom": src, "Conflicts": [{"Necessary": [{"Reads": ["r1"]}]}, {}]}, enrichment="record-rows"))
    case("enrichment: record rows with no source, a changed one and bare rows", lambda v: (
        add_enrichment(v, "enrichment/rows1.json", {"Conflicts": []}, enrichment="record-rows"),
        add_enrichment(v, "enrichment/rows2.json", {"derivedFrom": src + [{"path": "gone.json", "sha256": "x"}, {"sha256": "y"},
                                                                         {"path": "evidence", "sha256": "z"}, dict(src[0], sha256="0" * 64)],
                                                    "Conflicts": [{"Necessary": [{"Reads": []}, {"Reads": ["a"]}, {}]}]}, enrichment="record-rows"),
        add_enrichment(v, "enrichment/rows3.json", "not json", enrichment="order-variant"),
        add_enrichment(v, "enrichment/variant-tree.json", {"derivedFrom": src, "readForm": "tree", "Conflicts": [{"Necessary": [{}]}]}, enrichment="order-variant"),
        add_enrichment(v, "enrichment/rows-tree.json", {"derivedFrom": src, "readForm": "tree", "Conflicts": [{"Necessary": [{}]}]}, enrichment="record-rows")))
    case("enrichment: order geometry", lambda v: (
        add_enrichment(v, "enrichment/geo1.json", {"derivedFrom": src}, enrichment="order-geometry"),
        add_enrichment(v, "enrichment/geo2.json", {"derivedFrom": []}, enrichment="order-geometry"),
        add_enrichment(v, "enrichment/geo3.json", {"derivedFrom": [{"path": "nowhere.json", "sha256": "q"}, None]}, enrichment="order-geometry"),
        add_enrichment(v, "enrichment/geo4.json", "[]", enrichment="order-geometry")))
    case("enrichment: names from the plan's bytes and from others", lambda v: (
        with_plugins(v),
        add_enrichment(v, "enrichment/names.json", {"derivedFrom": [{"plugin": "A.esp", "sha256": plugin_sha}, {"plugin": "C.esp", "sha256": "cd" * 32},
                                                                    {"plugin": "D.esp"}], "unread": {"objects": [], "bases": []}}, enrichment="names"),
        manifest(lambda m: m.update(enrichmentStatus={"names": {"status": "enriched", "sentence": "names were read", "counts": {"objectsUnread": 0, "basesUnread": 0}}}))(v)))
    case("enrichment: a listed file that is absent is skipped", lambda v: (
        add_enrichment(v, "enrichment/rows.json", {"Conflicts": []}, enrichment="record-rows"), v.delete("enrichment/rows.json")))

    def names_status(st, names=None):
        def change(v):
            if names is not None:
                add_enrichment(v, "enrichment/names.json", names, enrichment="names")
            manifest(lambda m: m.update(enrichmentStatus=st))(v)
        return change

    case("names status: no statement", names_status({}))
    case("names status: not a statement", names_status({"names": "enriched"}))
    case("names status: an unknown word", names_status({"names": {"status": "done", "sentence": "x"}}))
    case("names status: no sentence", names_status({"names": {"status": "skipped", "sentence": "  "}}))
    case("names status: two lines", names_status({"names": {"status": "skipped", "sentence": "a\nb"}}))
    case("names status: enriched without files", names_status({"names": {"status": "enriched", "sentence": "read"}}))
    case("names status: skipped with files", names_status({"names": {"status": "skipped", "sentence": "not asked"}},
                                                           {"derivedFrom": [], "unread": {"objects": [], "bases": []}}))
    case("names status: no unread lists", names_status({"names": {"status": "enriched", "sentence": "read"}}, {"derivedFrom": []}))
    case("names status: names file not json", names_status({"names": {"status": "enriched", "sentence": "read"}}, "nope"))
    case("names status: partial said enriched", names_status(
        {"names": {"status": "enriched", "sentence": "read", "counts": {"objectsUnread": 0, "basesUnread": 0}}},
        {"derivedFrom": [], "unread": {"objects": ["0001:A.esp"], "bases": None}}))
    case("names status: partial with matching counts", names_status(
        {"names": {"status": "partial", "sentence": "some unread", "counts": {"objectsUnread": 1, "basesUnread": 2.0}}},
        {"derivedFrom": [], "unread": {"objects": ["0001:A.esp"], "bases": ["a", "b"]}}))
    case("names status: partial, objects only", names_status(
        {"names": {"status": "partial", "sentence": "some unread", "counts": {"objectsUnread": 1, "basesUnread": 0}}},
        {"derivedFrom": [], "unread": {"objects": ["0001:A.esp"], "bases": []}}))
    case("names status: counts absent", names_status({"names": {"status": "enriched", "sentence": "read"}},
                                                      {"derivedFrom": [], "unread": {"objects": [], "bases": []}}))
    return {"base": tree(base), "cases": cases}


SECTIONS = [("plans", plan_cases), ("ledger", ledger_cases), ("receipts", receipt_cases), ("identity", lambda tmp: identity_cases()),
            ("classification", classification_cases), ("dispositions", disposition_cases),
            ("pairs", pair_cases), ("finalize", lambda tmp: finalize_cases(tmp, None)), ("verify", verify_cases)]


def main() -> int:
    if os.name != "nt":
        print("make_known_answers_finalize.py records Windows path rules; run it on Windows.", file=sys.stderr)
        return 2
    with Run("make-known-answers-finalize", stage_source="single-stage"):
        return write_answers()


def write_answers() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-known-finalize-")).resolve()
    os.environ["HOUSECARL_RUNLOG_DIR"] = str(tmp / "runlog")   # so no run folder here becomes the run-log store
    try:
        doc = {"python": platform.python_version(), "placeholders": {"backslash": BACK, "forward": FWD, "doubled": DOUBLED}}
        for name, build in SECTIONS:
            doc[name] = build(tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    text = json.dumps(doc, indent=1) + "\n"
    for leak in (tmp.name, Path.home().name):
        assert leak not in text, f"the answers would keep a local path ({leak})"
    OUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
