"""Write evaluate.json: the known answers for `runcontrol run` and `resume`, computed by evalrun.py.

Every expected value comes from the real module: Validator.validate and _identity_checks over synthetic emits and logs,
resolve_spec, schedule, states_for, identity_drift, run_generic (with the child launch recorded instead of run), the
prerequisite functions, reuse_gap, changed_input, revalidate, Executor.account_stop, finish and consumed_for, and what
evalrun.py does with a run file of a shape it cannot read. The clock is replaced by a counter. Run it again only when
one of those rules changes on purpose, and commit the result with the change:

    python _geometry-harness/runcontrol/known-answers/make_known_answers_evaluate.py

Nothing outside a temporary folder is read or written except the harness sources and the output.
"""
from __future__ import annotations

import copy
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parents[2] / "scripts"))
from runlog import Run   # noqa: E402
import make_known_answers_finalize as F   # noqa: E402  (its plan, builder and masking helpers)
from make_known_answers_finalize import E   # noqa: E402

OUT = HERE / "evaluate.json"
T0 = 1767225600.0   # 2026-01-01T00:00:00Z: every fixture file's time of writing, unless a case says otherwise
NAMES = {"baseline": ["Skyrim.esm", "A.esp", "B.esp", "Target.esp"], "candidate-01": ["Skyrim.esm", "B.esp", "A.esp", "Target.esp"]}
PROVIDERS = [{"name": "ModA", "kind": "mod"}, {"name": "Data", "kind": "data"}]


def plugin_sha(name: str) -> str:
    return F.sha(("plugin " + name).encode())


def snapshot(root: Path) -> list:
    """Every file under root for the C# side to rebuild: text with the root masked, plan.sha256 as a row to recompute."""
    rows = []
    for p in sorted(root.rglob("*")):
        if not p.is_file():
            continue
        rel = p.relative_to(root).as_posix()
        if rel == "plan.sha256":
            rows.append({"path": rel, "planSha": True})
            continue
        try:
            row = {"path": rel, "text": F.pack(F.mask(p.read_bytes().decode("utf-8"), root))}
        except UnicodeDecodeError:
            row = {"path": rel, "bytes": F.packb(p.read_bytes())}
        if abs(p.stat().st_mtime - T0) < 86400:   # a time of writing a case set on purpose; any other is this run's own
            row["mtime"] = p.stat().st_mtime
        rows.append(row)
    return rows


def packed_json(doc, root: Path):
    """A document as masked JSON text, deflated when long."""
    return F.pack(json.dumps(F.mask(doc, root)))


def put(root: Path, rel: str, data, mtime: float = T0) -> Path:
    p = F.write(root, rel, data if isinstance(data, (str, bytes)) else json.dumps(data, indent=1))
    os.utime(p, (mtime, mtime))
    return p


def eval_plan(root: Path, checks: list, **over) -> dict:
    """F.run_plan's plan with what `run` reads besides: options, tools, the scope's configuration and each arm's request."""
    plan = F.run_plan(root, checks)
    rows = {}
    for arm in plan["arms"]:
        cand = put(root, f"candidates/{arm['candidateFile']}", json.dumps({"Plugins": NAMES[arm["armId"]]}))
        arm["candidateSha256"] = F.sha(cand.read_bytes())
        rows[arm["armId"]] = [{"name": n, "path": str(root / "plugins" / n), "sha256": plugin_sha(n), "hashSource": "scope"}
                              for n in NAMES[arm["armId"]]]
        arm["requested"] = {"plugins": rows[arm["armId"]], "providers": copy.deepcopy(PROVIDERS), "overlays": [],
                            "population": "load-time"}
        arm["expected"] = {"population": "load-time", "states": ["baseline"]}
    for n in NAMES["baseline"]:
        put(root, f"plugins/{n}", "plugin " + n)
    plan["options"] = {"instance": None, "profile": "Default", "game": None, "harness": "H:/no-harness", "probeArgs": "",
                       "probeCmd": {}, "diffTolerance": None, "noReverse": False, "population": "load-time"}
    plan["tools"] = {"contactprobe": {"exe": "H:/no-harness/contactprobe.exe", "exeSha256": None, "dllSha256": None,
                                      "coreDllSha256": None, "present": False}}
    plan["configuration"] = {"fingerprint": "f" * 64, "source": "candidate:candidate-00-baseline.json",
                             "plugins": [{"name": r["name"], "path": r["path"], "sha256": r["sha256"]} for r in rows["baseline"]],
                             "providers": copy.deepcopy(PROVIDERS), "overlays": [], "population": "load-time"}
    plan["ordersweepProbes"] = []
    plan["population"] = {"scoped": False}
    plan["inputs"] = {}
    plan.update(over)
    return plan


def emit_doc(root: Path, probe: str, arm: str = "baseline", *, pop="load-time", state="baseline", source=None, overlays=(),
             conflicts=(), coverage=None, names=None, hashed_pop="same", hashed_state="same") -> dict:
    """A conflict file as a probe writes it, its fingerprint by evalrun's own spelling of the probes' rule."""
    names = names or NAMES[arm]
    plugins = [{"Name": n, "Path": str(root / "plugins" / n), "Sha256": plugin_sha(n)} for n in names]
    fp = E.configuration_fingerprint([{"name": p["Name"], "sha256": p["Sha256"]} for p in plugins], PROVIDERS,
                                     [{"kind": o["Kind"], "sha256": o["Sha256"]} for o in overlays],
                                     pop if hashed_pop == "same" else hashed_pop, state if hashed_state == "same" else hashed_state)
    cfile = "candidate-00-baseline.json" if arm == "baseline" else "candidate-01-flip.json"
    return {"Probe": probe, "Conflicts": list(conflicts),
            "Coverage": coverage if coverage is not None else [{"Population": "references", "Judged": 3, "Unjudgeable": {}}],
            "Configuration": {"SchemaVersion": 1, "Fingerprint": fp, "Source": source if source is not None else f"candidate:{cfile}",
                              "Plugins": plugins, "Providers": [{"Name": v["name"], "Kind": v["kind"]} for v in PROVIDERS],
                              "Overlays": list(overlays), "Population": pop, "State": state, "Limits": []}}


CF = {"kind": "probe-emit", "exe": "contactprobe"}
SC = {"kind": "scopeprobe", "exe": "scopeprobe", "output": "json"}
PJ = {"kind": "probe-json", "exe": "depprobe", "output": "json",
      "result": {"kind": "catalogue", "findings": "edges", "judged": "summary.judged", "unjudgeable": "summary.withheld", "unit": "edges"}}
PJF = {"kind": "probe-json", "exe": "bosprobe", "output": "json", "result": {"findings": "rows", "judged": "counts.0"}}
PT = {"kind": "probe-target", "exe": "blockprobe", "output": "text", "files": ["{out}/{arm}.rows.json"],
      "result": {"findings": "regex:^cut: (?P<n>[\\d,]+)$", "judged": "regex:^judged (?P<n>\\d+)", "unjudgeable": "regex:^unjudged (?P<n>\\d+)"}}
PP = {"kind": "patchprobe", "exe": "patchprobe", "output": "json", "result": {"kind": "discovery", "findings": "hits"}}
AD = {"kind": "adviceprobe", "exe": "adviceprobe", "output": "json", "result": {"findings": ["rules", "clashes"]}}


# ------------------------------------------------------------------------------------------ small tables

def resolve_cases(_tmp) -> list:
    doc = {"edges": [1, 2, 3], "summary": {"judged": 7, "withheld": 0, "flag": True, "ratio": 3.0, "big": 10 ** 30, "name": "x"},
           "rows": {"a": 1, "b": 2}, "list": [{"n": 4}, {"n": 5}], "none": None}
    log = "$ probe\n(exit 0, 1.0s)\n\ncut: 1,234\njudged 12 of 13\nunjudged x\n  indented 9\n"
    specs = ["edges", "summary.judged", "summary.withheld", "summary.flag", "summary.ratio", "summary.big", "summary.name", "rows",
             "list.1.n", "list.2.n", "list.x", "none", "absent.path", "", ["edges", "rows"], ["edges", "absent"], [], ["regex:x"],
             ["edges", 3], 5, None, "regex:^cut: (?P<n>[\\d,]+)$", "regex:^judged (?P<n>\\d+)", "regex:^unjudged (?P<n>\\w+)",
             "regex:^nothing (?P<n>\\d+)", "regex:^judged (\\d+)", "regex:^cut: (?P<n>[\\d,]+)", "regex:indented (?P<n>\\d+)$",
             "regex:^(?P<n>\\d+)?judged", "regex:(unclosed"]
    rows = []
    for spec in specs:
        for d, lg in ((doc, log), (None, log), (doc, None)):
            val, why = E.resolve_spec(spec, d, lg)
            rows.append({"spec": spec, "doc": d is not None, "log": lg is not None, "value": val,
                         "why": why if "does not compile" not in why else "the pattern does not compile: "})
    return [{"doc": doc, "log": log, "rows": rows}]


def schedule_cases(_tmp) -> list:
    def c(cid, pre=(), required=False):
        return {"checkId": cid, "prerequisites": list(pre), "required": required}
    plans = [
        [c("opt.pool"), c("req.masters", required=True), c("req.sweep", ["req.masters"], True), c("opt.dep", ["req.masters"])],
        [c("a", ["b"], True), c("b", ["a"], True), c("c")],
        [c("a", ["nowhere"], True), c("b", ["a"]), c("c", required=True)],
        [c("z", ["y"]), c("y", ["x"]), c("x"), c("w", required=True), c("v", ["w"], True)],
        [c("only")],
        [],
        [c("a", required=True), c("b", ["a", "c"], True), c("c", ["d"]), c("d", ["b"]), c("e")],
        [{"checkId": "noPre", "required": True}, {"checkId": "nullPre", "prerequisites": None}, c("last", ["noPre", "nullPre"], True)],
    ]
    return [{"checks": p, "order": [x["checkId"] for x in E.schedule({"checks": p})]} for p in plans]


def states_cases(_tmp) -> list:
    rows = []
    for inputs in ({"states": "selected"}, {"states": "baseline"}, {}, None):
        for selected in (["baseline"], ["baseline", "QuestDone", "BASELINE", "Flood"], [], None, ["Baseline", 7]):
            check = {"checkId": "s", "inputs": inputs} if inputs is not None else {"checkId": "s"}
            plan = {"states": {"selected": selected}}
            rows.append({"check": check, "plan": plan, "states": E.states_for(check, plan)})
    for adapter in ({"kind": "probe-emit"}, {"kind": "probe-target"}, {"kind": "patchprobe", "commonFlags": True},
                    {"kind": "scopeprobe", "commonFlags": False}, {"kind": "probe-json", "commonFlags": 0}, {}):
        rows.append({"adapter": adapter, "common": E.wants_common_flags(adapter)})
    return rows


def drift_cases(_tmp) -> list:
    good = {"kind": "package", "state": "present", "locator": "H:\\h\\depprobe", "digest": "folder:" + "a" * 64,
            "files": [{"path": "depprobe.dll", "sha256": "1" * 64}, {"path": "old.dll", "sha256": "2" * 64}]}
    moved = dict(good, digest="folder:" + "b" * 64, files=[{"path": "depprobe.dll", "sha256": "3" * 64}, {"path": "new.dll", "sha256": "4" * 64}])
    absent = {"kind": "package", "state": "absent", "locator": "H:\\h\\depprobe", "digest": None, "files": []}
    cmd = {"kind": "probe-cmd", "state": "present", "command": "python fake.py", "digest": "probe-cmd:" + "c" * 64,
           "inputs": [{"token": 1, "path": "fake.py", "sha256": "5" * 64}]}
    loose = {"kind": "probe-cmd", "state": "unverifiable", "command": "echo it's", "digest": None, "inputs": []}
    pairs = [(good, good), (good, moved), (good, absent), (absent, absent), (absent, good), (None, good), ("text", good),
             ({"state": "present"}, good), ({"state": "odd", "digest": "x"}, good), (loose, cmd), (cmd, loose),
             (cmd, dict(cmd, digest="probe-cmd:" + "d" * 64, command="python other.py", inputs=[{"token": 1, "path": "other.py", "sha256": "6" * 64}])),
             (dict(good, digest="folder:" + "a" * 64), dict(good, files=[]))]
    rows = []
    for pid in ("probe:depprobe", "helper:conflictdiff", "controller"):
        for rec, now in pairs:
            rows.append({"pid": pid, "recorded": rec, "now": now, "want": E.identity_drift(pid, rec, now)})
    return rows


# ------------------------------------------------------------------------------------------ the validator

def validate_cases(tmp: Path) -> list:
    cases = []

    def case(name, calls, **plan_over):
        """One Validator over a sequence of calls; each call's files are written just before it."""
        root = tmp / f"v{len(cases):03d}"
        root.mkdir()
        plan = eval_plan(root, [], **{k: v(root) if callable(v) else v for k, v in plan_over.items()})
        v = E.Validator(root, plan)
        arms = {a["armId"]: a for a in plan["arms"]}
        steps = []
        for c in calls(root):
            files = []
            for rel, body in (c.get("files") or {}).items():
                mtime = T0 + c.get("mtime", {}).get(rel, 0)
                p = put(root, rel, body, mtime)
                files.append({"path": rel, "text": F.pack(F.mask(p.read_text(encoding="utf-8"), root)), "mtime": mtime})
            outputs = c.get("outputs", [])
            pre = {rel: (F.sha((root / rel).read_bytes()) if how == "same" else how) for rel, how in (c.get("pre") or {}).items()}
            started = None if c.get("started", 0) is None else T0 + c.get("started", -100)
            adapter, arm = c.get("adapter", CF), arms[c.get("arm", "baseline")]
            check = {"checkId": c.get("check", "g.check")}
            got = v.validate(check=check, arm=arm, part=c.get("part"), adapter=adapter, exit_code=c.get("exit", 0),
                             log_text=c.get("log", ""), outputs=[root / o for o in outputs],
                             pre_hashes={str(root / rel): h for rel, h in pre.items()}, started=started)
            steps.append({"files": files, "check": check["checkId"], "arm": arm["armId"], "part": c.get("part"), "adapter": adapter,
                          "exit": c.get("exit", 0), "log": c.get("log", ""), "outputs": outputs, "pre": c.get("pre") or {}, "started": started,
                          "want": F.mask(got, root)})
        cases.append({"name": name, "plan": packed_json(plan, root), "steps": steps})

    E0 = "outputs/g.check/baseline.json"

    def one(name, doc, **call):
        over = {k: call.pop(k) for k in list(call) if k in ("targets", "arms")}
        case(name, lambda root: [dict({"files": {E0: doc(root) if callable(doc) else doc}, "outputs": [E0]}, **call)], **over)

    def damaged(fn, **kw):
        def make(root):
            doc = emit_doc(root, "contactprobe", **kw)
            fn(doc, root)
            return doc
        return make

    one("an accepted emit with no conflict", lambda r: emit_doc(r, "contactprobe"))
    one("an accepted emit with two conflicts", lambda r: emit_doc(r, "contactprobe", conflicts=[{"Kind": "x"}, {"Kind": "y"}]))
    one("lower-case configuration keys", damaged(lambda d, r: d.__setitem__("Configuration", None) or d.__setitem__("configuration", {
        "fingerprint": emit_doc(r, "contactprobe")["Configuration"]["Fingerprint"], "source": "candidate:candidate-00-baseline.json",
        "plugins": [{"name": n, "path": "p", "sha256": plugin_sha(n)} for n in NAMES["baseline"]], "providers": PROVIDERS,
        "overlays": [], "population": "load-time", "state": "baseline"})))
    case("the same key twice is a duplicate receipt", lambda r: [
        {"files": {E0: emit_doc(r, "contactprobe")}, "outputs": [E0]}, {"files": {E0: emit_doc(r, "contactprobe")}, "outputs": [E0]}])
    one("a refusal marker, exit 2 and no output: the configuration is illegal", {}, files={}, outputs=[E0], exit=2,
        log="$ probe\n(exit 2, 0.1s)\n\n  MISSING MASTER: B.esp needs C.esm, which loads after it\n", adapter=SC)
    one("the same from a probe that reads no order is a refusal", {}, files={}, outputs=[E0], exit=2,
        log="REFUSED: no pool\n", adapter=PP)
    one("a refusal marker with exit 0 and no output", {}, files={}, outputs=[E0], exit=0, log="---- stderr ----\n! REFUSED twice\n")
    one("a refusal marker beside a valid output is prose", lambda r: emit_doc(r, "contactprobe"),
        log="a function the decompiler REFUSED 0 times\nCANNOT ENABLE nothing\n")
    one("a marker past forty characters is not a marker", lambda r: emit_doc(r, "contactprobe"), exit=3,
        log="x" * 41 + " REFUSED\n" + "y" * 40 + "PROFILE MISMATCH\n")
    one("a non-zero exit with its output", lambda r: emit_doc(r, "contactprobe"), exit=4294967295)
    one("no exit code and no output", {}, files={}, outputs=[E0, "outputs/g.check/second.json"], exit=None)
    one("an output older than the launch", lambda r: emit_doc(r, "contactprobe"), started=100)
    one("an output within two seconds before the launch", lambda r: emit_doc(r, "contactprobe"), started=1.5)
    one("an output byte-identical to the one before the launch", lambda r: emit_doc(r, "contactprobe"), pre={E0: "same"})
    one("an output that replaced the one before the launch", lambda r: emit_doc(r, "contactprobe"), pre={E0: "0" * 64})
    one("no launch time: staleness is not judged", lambda r: emit_doc(r, "contactprobe"), started=None, pre={E0: "same"})
    one("an output that is not JSON", "not json")
    one("an output that is a JSON list", [1, 2])
    one("no Probe or Conflicts", {"Probe": "contactprobe"})
    one("another probe's emit", lambda r: emit_doc(r, "OverlapProbe"))
    one("the probe's name in another case", lambda r: emit_doc(r, "ContactProbe"))
    one("no Configuration", damaged(lambda d, r: d.pop("Configuration")))
    one("a Configuration with no plugins", damaged(lambda d, r: d["Configuration"].__setitem__("Plugins", [])))
    one("a plugin with no name, another with no hash", damaged(lambda d, r: (d["Configuration"]["Plugins"][0].__setitem__("Name", "  "),
                                                                          d["Configuration"]["Plugins"][1].__setitem__("Sha256", "abc"))))
    one("no Coverage", damaged(lambda d, r: d.__setitem__("Coverage", [])))
    one("the first population judged nothing and withheld some", lambda r: emit_doc(r, "contactprobe", coverage=[
        {"Population": "refs", "Judged": 0, "Unjudgeable": {"no-mesh": 2, "other": None}}, {"population": "more", "judged": 5}]))
    one("every population judged nothing", lambda r: emit_doc(r, "contactprobe", coverage=[{"population": "a", "judged": 0}, {"Population": "b"}]))
    one("a later population judged nothing", lambda r: emit_doc(r, "contactprobe", coverage=[
        {"Population": "a", "Judged": "4"}, {"Population": "b", "Judged": 0, "Unjudgeable": {"x": 9}}]))
    one("the same names in another order", lambda r: emit_doc(r, "contactprobe", names=NAMES["candidate-01"]))
    one("a missing and an extra plugin", lambda r: emit_doc(r, "contactprobe", names=["Skyrim.esm", "A.esp", "Zed.esp", "B.esp"]))
    one("one plugin more than requested", lambda r: emit_doc(r, "contactprobe", names=NAMES["baseline"] + ["Late.esp"]))
    one("names in another case are the same names", damaged(lambda d, r: d["Configuration"]["Plugins"][1].__setitem__("Name", "a.ESP")))
    one("a plugin consumed at another hash", damaged(lambda d, r: d["Configuration"]["Plugins"][2].__setitem__("Sha256", "9" * 64)))
    one("the same bytes read at another path", damaged(lambda d, r: d["Configuration"]["Plugins"][1].__setitem__("Path", str(r / "elsewhere" / "A.esp"))))
    one("the same path in another spelling", damaged(lambda d, r: d["Configuration"]["Plugins"][1].__setitem__(
        "Path", str(r / "plugins" / "." / "a.ESP").replace("\\", "/"))))
    one("a hash the plan could not state", lambda r: emit_doc(r, "contactprobe"), arms=lambda r: unstated(eval_plan(r, [])["arms"]))
    one("the plan states no request", lambda r: emit_doc(r, "contactprobe"),
        arms=lambda r: [dict(a, requested={}) for a in eval_plan(r, [])["arms"]])
    one("an overlay the request does not have", lambda r: emit_doc(r, "contactprobe", overlays=[{"Kind": "bos-ini", "Path": "x", "Sha256": "A" * 64}]))
    one("a fingerprint that is not the digest of the configuration", damaged(lambda d, r: d["Configuration"].__setitem__("Fingerprint", "0" * 64)))
    one("an upper-case fingerprint is the same fingerprint", damaged(
        lambda d, r: d["Configuration"].__setitem__("Fingerprint", d["Configuration"]["Fingerprint"].upper())))
    one("a source naming another candidate", lambda r: emit_doc(r, "contactprobe", source="candidate:candidate-07.json"))
    one("a source holding the candidate's stem", lambda r: emit_doc(r, "contactprobe", source="profile:Default+candidate-00-baseline"))
    one("no source at all", lambda r: emit_doc(r, "contactprobe", source=""))
    one("another population than the arm's", lambda r: emit_doc(r, "contactprobe", pop="flags"))
    one("a population the adapter declares", lambda r: emit_doc(r, "contactprobe", pop="flags", state="-"),
        adapter=dict(CF, population="flags", state="-"))
    one("a state the plan did not select", lambda r: emit_doc(r, "contactprobe", state="QuestDone"))
    one("the state '-' is always allowed", lambda r: emit_doc(r, "contactprobe", state="-"))
    case("two fingerprints for one arm, population and state", lambda r: [
        {"files": {E0: emit_doc(r, "contactprobe")}, "outputs": [E0], "check": "g.one"},
        {"files": {E0: emit_doc(r, "contactprobe", overlays=[])}, "outputs": [E0], "check": "g.two"},
        {"files": {E0: other_fingerprint(r)}, "outputs": [E0], "check": "g.three"},
        {"files": {E0: emit_doc(r, "contactprobe", pop="flags", state="-")}, "outputs": [E0], "check": "g.four",
         "adapter": dict(CF, population="flags", state="-")}])
    case("one plugin at two hashes within an arm", lambda r: [
        {"files": {E0: emit_doc(r, "contactprobe")}, "outputs": [E0], "check": "g.one"},
        {"files": {E0: other_hash(r)}, "outputs": [E0], "check": "g.two"},
        {"files": {"outputs/g.check/candidate-01.json": other_hash(r, "candidate-01")}, "outputs": ["outputs/g.check/candidate-01.json"],
         "check": "g.two", "arm": "candidate-01"}])
    case("a reverse part is held to the baseline's request", lambda r: [
        {"files": {E0: emit_doc(r, "contactprobe")}, "outputs": [E0], "arm": "candidate-01", "part": "reverse"},
        {"files": {E0: emit_doc(r, "contactprobe", "candidate-01")}, "outputs": [E0], "arm": "candidate-01", "part": "reverse", "check": "g.b"},
        {"files": {E0: emit_doc(r, "contactprobe", "candidate-01")}, "outputs": [E0], "arm": "candidate-01", "part": "arm", "check": "g.c"}])

    # a json output read through adapter.result
    J = "outputs/g.check/baseline.json"
    one("a catalogue's rows are evidence", {"edges": [1, 2], "summary": {"judged": 5, "withheld": 1}}, adapter=PJ)
    one("a catalogue that judged nothing and withheld some", {"edges": [], "summary": {"judged": 0, "withheld": 3}}, adapter=PJ)
    one("a result path that does not resolve", {"edges": [], "summary": {"judged": True}}, adapter=PJ)
    one("findings and no judged count", {"rows": {"a": 1}, "counts": ["x"]}, adapter=PJF)
    one("no findings", {"rows": [], "counts": [4]}, adapter=PJF)
    one("a discovery", {"hits": [{"file": "p.7z"}]}, adapter=PP)
    one("a list spec is a sum", {"rules": [1, 2], "clashes": [3]}, adapter=AD)
    one("an adapter with no result block", {"anything": 1}, adapter={"kind": "probe-json", "exe": "x", "output": "json"})

    # scopeprobe
    def scope_doc(r, complete=False, failures=("'Extra.esp' is not in the resolved configuration (4 plugins)",), resolved=None,
                  measured=("Target.esp",)):
        return {"complete": complete, "walkFailures": list(failures),
                "configuration": {"plugins": [{"name": n} for n in (resolved if resolved is not None else NAMES["baseline"])]},
                "targets": [{"name": n} for n in measured]}
    two = [{"name": "Target.esp"}, {"name": "Extra.esp"}]
    one("a complete scope", lambda r: scope_doc(r, True, ()), adapter=SC, exit=0)
    one("an incomplete scope with exit 0 is inconclusive", lambda r: scope_doc(r), adapter=SC, exit=0)
    one("exit 1 explained by a target the arm leaves out", lambda r: scope_doc(r), adapter=SC, exit=1, targets=two)
    one("exit 1 with another walk failure", lambda r: scope_doc(r, failures=("'Extra.esp' is not in the resolved configuration (4)", "LAND unreadable")),
        adapter=SC, exit=1, targets=two)
    one("exit 1 with no walk failure", lambda r: scope_doc(r, failures=()), adapter=SC, exit=1, targets=two)
    one("exit 1 on another resolved order", lambda r: scope_doc(r, resolved=NAMES["candidate-01"]), adapter=SC, exit=1, targets=two)
    one("exit 1 with a loaded target not measured", lambda r: scope_doc(r, measured=()), adapter=SC, exit=1, targets=two)
    one("exit 1 where the omitted target did resolve", lambda r: scope_doc(r, resolved=NAMES["baseline"] + ["Extra.esp"]), adapter=SC, exit=1, targets=two)
    one("exit 1 with a scope that is a list", [1], adapter=SC, exit=1, targets=two)
    one("a scope with no complete key", {"targets": []}, adapter=SC, exit=0)

    # a text probe: the log is the output, then the files it writes
    L, R = "logs/g.check__baseline.txt", "outputs/g.check/baseline.rows.json"
    good_log = "$ blockprobe\n(exit 0, 1.0s)\n\ncut: 1,204\njudged 12 of 13\nunjudged 0\n"
    case("a text probe's printed counts", lambda r: [{"files": {L: good_log, R: "[]"}, "outputs": [L, R], "adapter": PT}])
    case("a text probe that judged nothing", lambda r: [{"files": {L: "cut: 0\njudged 0\nunjudged 4\n", R: "[]"}, "outputs": [L, R], "adapter": PT}])
    case("a text probe whose line is missing", lambda r: [{"files": {L: "judged 3\n", R: "[]"}, "outputs": [L, R], "adapter": PT}])
    case("a text probe's file left from an earlier run", lambda r: [
        {"files": {L: good_log, R: "[]"}, "outputs": [L, R], "adapter": PT, "mtime": {R: -500}},
        {"files": {L: good_log, R: "[1]"}, "outputs": [L, R], "adapter": PT, "pre": {R: "same"}, "check": "g.b"},
        {"files": {L: good_log}, "outputs": [L, "outputs/g.check/absent.rows.json"], "adapter": PT, "check": "g.c"}])
    return cases


def unstated(arms: list) -> list:
    arms[0]["requested"]["plugins"][1]["hashSource"] = "unstated"
    arms[0]["requested"]["plugins"][2]["sha256"] = None
    return arms


def other_fingerprint(root: Path) -> dict:
    """A consistent emit of the same arm, population and state with another provider list, so another fingerprint."""
    doc = emit_doc(root, "contactprobe")
    cfg = doc["Configuration"]
    cfg["Providers"] = [{"Name": "ModB", "Kind": "mod"}]
    cfg["Fingerprint"] = E.configuration_fingerprint([{"name": p["Name"], "sha256": p["Sha256"]} for p in cfg["Plugins"]],
                                                     [{"name": "ModB", "kind": "mod"}], [], cfg["Population"], cfg["State"])
    return doc


def other_hash(root: Path, arm: str = "baseline") -> dict:
    """An emit whose own configuration is consistent but states another hash for A.esp."""
    doc = emit_doc(root, "contactprobe", arm)
    cfg = doc["Configuration"]
    next(p for p in cfg["Plugins"] if p["Name"] == "A.esp")["Sha256"] = "7" * 64
    cfg["Fingerprint"] = E.configuration_fingerprint([{"name": p["Name"], "sha256": p["Sha256"]} for p in cfg["Plugins"]], PROVIDERS, [],
                                                     cfg["Population"], cfg["State"])
    return doc


def reuse_identity_cases(tmp: Path) -> list:
    """_identity_checks for an import declared as a reuse: only the candidate file name is relaxed, into a recorded limit."""
    root = tmp / "reuse-identity"
    root.mkdir()
    plan = eval_plan(root, [])
    rows = []
    for name, source, declared in (
            ("another candidate's capture", "candidate:candidate-09-other.json", {"reusedFrom": {"planId": "planXYZ", "armId": "candidate-09"}}),
            ("a capture with no source", "", {"reusedFrom": {"planId": "planXYZ", "armId": "candidate-09"}}),
            ("the same candidate file", "candidate:candidate-00-baseline.json", {"reusedFrom": {}}),
            ("a name holding this arm's stem", "candidate:candidate-00-baseline-alternate.json", {"origin": "reused"}),
            ("not declared", "candidate:candidate-09-other.json", None)):
        doc = emit_doc(root, "contactprobe", source=source)
        identity = E.Validator.emit_identity(doc)
        limits = []
        rej = E.Validator(root, plan)._identity_checks(identity, plan["arms"][0], doc, CF, None, limits, declared)
        rows.append({"name": name, "doc": F.mask(doc, root), "declared": declared, "rejections": F.mask(rej, root), "limits": F.mask(limits, root)})
    return [{"plan": packed_json(plan, root), "rows": rows}]


def owner_difference_cases(tmp: Path) -> list:
    """A null population or state: Python hashes the text None, the probes' owner adds no text. The port follows the owner."""
    root = tmp / "owner"
    root.mkdir()
    plan = eval_plan(root, [])
    rows = []
    for name, kw in (("a null population hashed as Python hashes it", dict(pop=None)),
                     ("a null population hashed as the probes' owner hashes it", dict(pop=None, hashed_pop="")),
                     ("a null state hashed as Python hashes it", dict(state=None)),
                     ("a null state hashed as the probes' owner hashes it", dict(state=None, hashed_state=""))):
        doc = emit_doc(root, "contactprobe", **kw)
        rel = "outputs/g.check/baseline.json"
        put(root, rel, doc)
        got = E.Validator(root, plan).validate(check={"checkId": "g.check"}, arm=plan["arms"][0], part=None, adapter=CF, exit_code=0,
                                               log_text="", outputs=[root / rel], pre_hashes={}, started=T0 - 100)
        rows.append({"name": name, "doc": F.mask(doc, root), "pythonAccepted": got["accepted"],
                     "pythonCauses": [r["cause"] for r in got["rejections"]]})
    return [{"plan": packed_json(plan, root), "rows": rows}]


# ------------------------------------------------------------------------------------------ generic rows

FAKES = {n: f"python fake/{n}.py" for n in ("scopeprobe", "contactprobe", "switchprobe", "blockprobe", "patchprobe", "adviceprobe", "stateprobe")}


def row(cid, kind, exe, args, **more):
    adapter = {"kind": kind, "exe": exe, "args": args}
    extra = {k: more.pop(k) for k in list(more) if k in ("inputs", "prerequisites")}
    adapter.update(more)
    return dict(F.check_row(cid, kind), adapter=adapter, **extra)


GENERIC_CHECKS = [
    row("l.masters", "scopeprobe", "scopeprobe", ["--order", "{order}", "--json", "{json}"], output="json"),
    row("l.masters-target", "scopeprobe", "scopeprobe", ["--target", "{target}", "--json", "{json}"], output="json"),
    row("g.empty", "probe-emit", "contactprobe", ["--empty-space", "{out}/{arm}.empty-space.json", "--run", "{run}"]),
    row("s.census", "probe-json", "switchprobe", ["--order", "{order}", "--json", "{json}"], output="json"),
    row("g.route", "probe-target", "blockprobe", ["{target}", "--rows", "{out}/{arm}.rows.json"], output="text",
        files=["{out}/{arm}.rows.json", "{run}/extra-{arm}.txt"], commonFlags=False),
    row("p.installed", "patchprobe", "patchprobe", ["--pair", "{pair}", "--json", "{json}"], output="json", commonFlags=False),
    row("p.bare-pair", "patchprobe", "patchprobe", ["{pair}"], output="json", commonFlags=True),
    row("p.advice", "adviceprobe", "adviceprobe", ["--mod", "{nexusId}", "--out", "{out}", "--offline"], output="json"),
    row("p.advice-online", "adviceprobe", "adviceprobe", ["--mod", "{nexusId}", "--out", "{out}", "--refresh", "online"], output="json"),
    row("p.advice-refresh", "adviceprobe", "adviceprobe", ["--mod", "{nexusId}", "--json", "{json}", "--refresh", "offline"], output="json"),
    row("p.advice-bare", "adviceprobe", "adviceprobe", ["--mod", "{nexusId}", "--refresh"], output="json"),
    row("s.states", "probe-emit", "stateprobe", ["--switch-map", "{switchMap}"], statesFlag="--state", statesFileFlag="--states-file",
        populationFlag="--pop", primitiveSolidityFlag="--primitive-solidity", inputs={"states": "selected"}),
    row("s.no-file-flag", "probe-emit", "stateprobe", ["--emit", "{emit}"], statesFlag="--state", inputs={"states": "selected"}),
    row("g.unbuilt", "probe-emit", "nosuchprobe", []),
]


def generic_cases(tmp: Path) -> list:
    cases = []

    def case(name, cid, aid="baseline", *, change=None, setup=None, intended=None):
        root = tmp / f"g{len(cases):03d}"
        root.mkdir()
        plan = eval_plan(root, copy.deepcopy(GENERIC_CHECKS))
        plan["options"]["probeCmd"] = dict(FAKES)
        plan["options"]["instance"], plan["options"]["game"] = "I:/instance", "G:/game"
        if change:
            change(plan, root)
        with F.fake_clock():
            b = F.Builder(root, plan)
            if setup:
                setup(b, root)
            tree = snapshot(root)
            led = E.Ledger(root, plan)
            led.run_id = "run-2"
            ex = E.Executor(root, plan, led, resume=False)
            ex.parent_run_id = "parent-2"
            seen = {}
            ex.run_child = lambda check, arm, part, argv, outputs, **kw: seen.update(
                argv=list(argv), outputs=[str(o) for o in outputs], states=kw.get("states"))
            check = next(c for c in plan["checks"] if c["checkId"] == cid)
            E.run_generic(ex, check, ex.arms[aid])
        if seen:
            want = {"launch": F.mask(seen, root)}
        else:
            e = led.entry(cid, aid, None)
            r = E.load_json(root / "receipts" / f"{E.Ledger.key(cid, aid, None)}.json")
            note = F.mask(r["result"]["note"], root)
            if e["status"] == "blocked(census-unreadable)":
                note = note.split("(")[0]   # the rest is the Python runtime's own words
            want = {"account": {"status": e["status"], "cause": e["cause"], "note": note}}
        if intended is not None:
            # the controller's own answer, where it was decided to behave otherwise than evalrun.py; Python's stays beside it
            want = {**intended, "python": want}
        written = {}
        for p in sorted((root / "outputs").rglob("*.states.*")) if (root / "outputs").is_dir() else []:
            text = p.read_bytes().decode("utf-8")
            if p.name.endswith(".json"):
                doc = json.loads(text)
                doc["generator"]["sha256"] = "<the controller>"
                text = json.dumps(doc, indent=1, ensure_ascii=False) + "\n"
            written[p.relative_to(root).as_posix()] = F.mask(text.replace("\r\n", "\n"), root)
        if isinstance((want.get("launch") or {}).get("states"), dict):
            for k in ("generatorSha256", "documentSha256"):
                want["launch"]["states"][k] = "<the controller>"
        cases.append({"name": name, "tree": tree, "check": cid, "arm": aid, "want": want, "written": written})

    def census(b, root, aid="baseline", switches=({"key": "000800:Target.esp", "edid": "Gate"},), status="completed", accepted=True, raw=None):
        rel = f"outputs/state.switch-census/{aid}.json"
        put(root, rel, raw if raw is not None else {"switches": list(switches), "jointFlips": []})
        b.settle("state.switch-census", aid, None, status, detector="catalogue", accepted=accepted, outputs=(rel,))

    def states(*names, depth="standard"):
        return lambda plan, root: plan.__setitem__("states", {"selected": list(names), "depth": depth, "notExplored": []})

    def both(*fns):
        return lambda plan, root: [fn(plan, root) for fn in fns]

    def population(plan, root, damage=False):
        p = put(root, "population.json", '{"schema": 1}\n')
        plan["population"] = {"scoped": True, "file": "population.json", "sha256": "0" * 64 if damage else F.sha(p.read_bytes()),
                              "scopedChecks": ["s.states"]}

    def table(plan, root, damage=False):
        p = put(root, "inputs/primitive-solidity.json", "[]")
        plan["inputs"] = {"primitiveSolidity": {"given": True, "file": "inputs/primitive-solidity.json",
                                                "sha256": "0" * 64 if damage else F.sha(p.read_bytes())}}

    case("scopeprobe: the order and each plan target", "l.masters", "candidate-01")
    case("scopeprobe: a target flag in the registry's own args", "l.masters-target")
    # Piranha 2026-11-19: a selection with no plugin is evaluated. The controller proves the arm's order on its last
    # plugin instead of blocking the row, so the checks that need the order can run; evalrun.py blocks it.
    case("scopeprobe: a plan with no target", "l.masters", change=lambda p, r: p.__setitem__("targets", ["odd", {"provider": "x"}]),
         intended={"launchTarget": "Target.esp"})

    def no_plugins(p, r):
        p["targets"] = []
        for a in p["arms"]:
            a["plugins"] = []
            a["requested"]["plugins"] = []
    case("scopeprobe: a plan with no target, on an arm that loads no plugin", "l.masters", change=no_plugins)
    case("scopeprobe: probe arguments from the plan's options", "l.masters", change=lambda p, r: p["options"].__setitem__("probeArgs", " --lean\t--threads 4 "))
    case("scopeprobe: no instance, profile or game", "l.masters",
         change=lambda p, r: p["options"].update(instance=None, profile=None, game=""))
    case("probe-emit: out, arm and run placeholders, the emit added", "g.empty", "candidate-01")
    case("probe-json: the json named by the registry", "s.census")
    case("probe-target: bare targets, the files it writes", "g.route", "candidate-01")
    case("probe-target: an arm that loads no target", "g.route",
         change=lambda p, r: p.__setitem__("targets", [{"name": "Elsewhere.esp"}]))
    case("probe-target: only the targets the arm loads", "g.route",
         change=lambda p, r: p.__setitem__("targets", [{"name": "Elsewhere.esp"}, {"name": "target.ESP"}, {"name": "A.esp"}]))
    case("patchprobe: the baseline gets every pair of the plan", "p.installed")
    case("patchprobe: a candidate gets its own pairs", "p.installed", "candidate-01")
    case("patchprobe: a bare pair placeholder", "p.bare-pair")
    case("patchprobe: no pair anywhere", "p.installed",
         change=lambda p, r: p["arms"][1].__setitem__("plugins", list(NAMES["baseline"])))
    case("patchprobe: a review arm with no clash to take a pair from", "p.installed", "candidate-01",
         change=lambda p, r: (p.__setitem__("reviewArm", "candidate-01"), p["arms"][1].__setitem__("plugins", list(NAMES["baseline"]))))
    case("adviceprobe: the Nexus id, the report it writes", "p.advice")
    case("adviceprobe: no Nexus id", "p.advice", change=lambda p, r: p.__setitem__("targets", [{"name": "Target.esp"}]))
    case("adviceprobe: a registry that asks to fetch", "p.advice-online")
    case("adviceprobe: refresh offline is allowed, and a json output", "p.advice-refresh")
    case("adviceprobe: a refresh flag with no word", "p.advice-bare")
    case("adviceprobe: the id under the footprint wins", "p.advice",
         change=lambda p, r: p.__setitem__("targets", [{"name": "T", "nexusId": 5, "footprint": {"nexusId": "777"}}]))
    case("state check: no state selected but the baseline", "s.states", setup=census)
    case("state check: each selected state by the flag", "s.states", setup=census, change=states("baseline", "QuestDone", "Flood"))
    case("state check: no accepted census, no switch map", "s.states", change=states("baseline", "QuestDone"))
    case("state check: a census that was rejected", "s.states", change=states("baseline", "QuestDone"),
         setup=lambda b, r: census(b, r, status="rejected(exit-nonzero)", accepted=False))
    case("state check: the frozen population file", "s.states", setup=census, change=both(states("baseline", "QuestDone"), population))
    case("state check: a population file that changed", "s.states", setup=census,
         change=both(states("baseline", "QuestDone"), lambda p, r: population(p, r, True)))
    case("state check: the frozen primitive table", "s.states", setup=census, change=both(states("baseline", "QuestDone"), table))
    case("state check: a primitive table that changed", "s.states", setup=census,
         change=both(states("baseline", "QuestDone"), lambda p, r: table(p, r, True)))
    case("deep: the states generated from the arm's census", "s.states", "candidate-01", change=states("baseline", depth="deep"),
         setup=lambda b, r: (census(b, r), census(b, r, "candidate-01", switches=(
             {"key": "000800:Target.esp", "edid": "Gate"}, {"key": "000801:Target.esp"}, {"edid": "NoKey"}))))
    case("deep: no accepted census on this arm", "s.states", "candidate-01", change=states("baseline", depth="deep"), setup=census)
    case("deep: a registry row with no states-file flag", "s.no-file-flag", change=states("baseline", depth="deep"), setup=census)
    case("deep: a census with nothing to flip", "s.states", change=states("baseline", depth="deep"),
         setup=lambda b, r: census(b, r, switches=({"edid": "NoKey"},)))
    case("deep: a census that is not JSON", "s.states", change=states("baseline", depth="deep"),
         setup=lambda b, r: census(b, r, raw="not json"))
    case("a probe with no build and no stand-in", "g.unbuilt")
    case("a candidate file that changed since the plan", "g.empty", setup=lambda b, r: put(r, "candidates/candidate-00-baseline.json", "{}"))
    case("a candidate file that is gone", "g.empty", setup=lambda b, r: (r / "candidates" / "candidate-00-baseline.json").unlink())
    return cases


# ------------------------------------------------------------------------------------------ prerequisites, reuse

def prereq_cases(tmp: Path) -> list:
    root = tmp / "prereq"
    root.mkdir()
    combos = [(None, "completed"), ("rejected(exit-nonzero)", "completed"), ("failed", "reused"), ("blocked(prerequisite:l.x)", "completed"),
              ("blocked(tool-missing)", "completed"), ("not-applicable", None), (None, None), ("completed", "rejected(refused)"),
              ("skipped-budget", "completed"), ("stopped(deadline)", "not-applicable"), (None, "not-applicable (imported)"),
              ("reused", None), ("invalidated(x)", "reused"), ("rejected(tool-changed)", "rejected(tool-changed)")]
    checks = [dict(F.check_row(f"p{i}", "scopeprobe")) for i in range(len(combos))]
    checks += [dict(F.check_row(f"c{i}", "probe-json"), prerequisites=[f"p{i}"]) for i in range(len(combos))]
    checks += [dict(F.check_row("both", "probe-json"), prerequisites=["p0", "p1", "p5"]), dict(F.check_row("free", "probe-json"))]
    plan = eval_plan(root, checks)
    with F.fake_clock():
        b = F.Builder(root, plan)
        for i, (cand, base) in enumerate(combos):
            for aid, status in (("candidate-01", cand), ("baseline", base)):
                if status is not None:
                    b.settle(f"p{i}", aid, None, status, receipt=False)
    ex = SimpleNamespace(ledger=E.Ledger(root, plan))
    rows = []
    for c in checks:
        if not c["prerequisites"]:
            continue
        for aid in ("candidate-01", "baseline"):
            pre = c["prerequisites"][0]
            rows.append({"check": c["checkId"], "arm": aid, "pre": pre, "decided": E.decided_on_arm(ex, pre, aid),
                         "arms": list(E.prereq_answer_arms(ex, pre, aid)), "detail": E.prereq_block_detail(ex, pre, aid),
                         "satisfied": E.prereq_satisfied(ex, c, aid), "reuseBlocked": E.reuse_blocked_by(ex, c, aid)})
    return [{"tree": snapshot(root), "rows": rows}]


def reuse_cases(tmp: Path) -> dict:
    root = tmp / "reuse"
    root.mkdir()
    for rel in ("plugins/A.esp", "plugins/B.esp", "outputs/c/a.json", "sweep/base.json", "candidates/c.json", "tool/x.dll",
                "tool/housecarl-core.dll", "overlay/_swap.ini"):
        put(root, rel, "bytes of " + rel)
    sha = lambda rel: F.sha((root / rel).read_bytes())   # noqa: E731
    P = lambda rel: str(root / rel)   # noqa: E731
    good = {"validation": {"accepted": True},
            "outputs": [{"path": "outputs/c/a.json", "sha256": sha("outputs/c/a.json")}],
            "consumed": {"candidatePath": P("candidates/c.json"), "candidateSha256": sha("candidates/c.json"),
                         "configuration": {"plugins": [{"name": "A.esp", "path": P("plugins/A.esp"), "sha256": sha("plugins/A.esp")},
                                                       {"name": "B.esp", "path": P("plugins/B.esp"), "sha256": sha("plugins/B.esp")}],
                                           "overlays": [{"kind": "bos-ini", "path": P("overlay/_swap.ini"), "sha256": sha("overlay/_swap.ini")}]},
                         "tool": {"present": True, "exe": P("tool/x.exe"), "dllSha256": sha("tool/x.dll"),
                                  "coreDllSha256": sha("tool/housecarl-core.dll")},
                         "baseEmitPath": "sweep/base.json", "baseEmitSha256": sha("sweep/base.json"),
                         "inputs": [{"part": "reverse", "path": "sweep/base.json", "sha256": sha("sweep/base.json")}]}}

    def variant(name, fn):
        r = copy.deepcopy(good)
        fn(r)
        return {"name": name, "receipt": F.mask(r, root), "gap": F.mask(E.reuse_gap(r), root), "changed": F.mask(E.changed_input(root, r), root)}

    c = lambda r: r["consumed"]   # noqa: E731
    cfg = lambda r: r["consumed"]["configuration"]   # noqa: E731
    rows = [
        variant("everything stated and unchanged", lambda r: None),
        variant("a receipt that was not accepted", lambda r: (r.__setitem__("validation", {"accepted": False}), cfg(r).__setitem__("plugins", []))),
        variant("a stated reuse gap", lambda r: cfg(r).__setitem__("reuseGap", "scope.json states nothing")),
        variant("no consumed plugins", lambda r: cfg(r).__setitem__("plugins", [])),
        variant("a plugin with no path, another with a short hash, one unnamed", lambda r: cfg(r).__setitem__("plugins", [
            {"name": "A.esp", "sha256": sha("plugins/A.esp")}, {"name": "B.esp", "path": P("plugins/B.esp"), "sha256": "abc"}, {"path": "x"}])),
        variant("an overlay with no hash, another with no kind", lambda r: cfg(r).__setitem__("overlays", [
            {"kind": "bos-ini", "path": "x"}, {"sha256": "1" * 64}])),
        variant("an output with no hash", lambda r: r["outputs"].append({"path": "outputs/c/missing.json", "sha256": None})),
        variant("a tool present with no assembly hash", lambda r: c(r)["tool"].__setitem__("dllSha256", None)),
        variant("a tool with no exe named", lambda r: c(r).__setitem__("tool", {"present": True})),
        variant("a tool that was absent", lambda r: c(r).__setitem__("tool", {"present": False, "exe": P("tool/x.exe")})),
        variant("a base emit with no hash", lambda r: c(r).__setitem__("baseEmitSha256", None)),
        variant("a comparison input with no hash, one with no part", lambda r: c(r).__setitem__("inputs", [
            {"part": "reverse", "path": "sweep/r.json"}, {"path": "sweep/b.json", "sha256": "zz"}, {"part": "arm"}])),
        variant("an output that changed", lambda r: r["outputs"][0].__setitem__("sha256", "1" * 64)),
        variant("an output that is gone", lambda r: r["outputs"][0].__setitem__("path", "outputs/c/gone.json")),
        variant("a candidate file that changed", lambda r: c(r).__setitem__("candidateSha256", "2" * 64)),
        variant("a candidate file that is gone", lambda r: c(r).__setitem__("candidatePath", P("candidates/gone.json"))),
        variant("a plugin that changed and one that is gone", lambda r: (cfg(r)["plugins"][0].__setitem__("sha256", "3" * 64),
                                                                        cfg(r)["plugins"][1].__setitem__("path", P("plugins/Gone.esp")))),
        variant("an overlay that changed", lambda r: cfg(r)["overlays"][0].__setitem__("sha256", "4" * 64)),
        variant("the probe's assembly and the core assembly changed", lambda r: c(r)["tool"].update(dllSha256="5" * 64, coreDllSha256="6" * 64)),
        variant("the probe's folder is gone", lambda r: c(r)["tool"].__setitem__("exe", P("nowhere/x.exe"))),
        variant("a base emit and an input that changed", lambda r: (c(r).__setitem__("baseEmitSha256", "7" * 64),
                                                                     c(r)["inputs"][0].__setitem__("sha256", "8" * 64))),
        variant("several changes at once", lambda r: (r["outputs"][0].__setitem__("sha256", "1" * 64), c(r).__setitem__("candidateSha256", "2" * 64))),
    ]
    return {"tree": snapshot(root), "rows": rows}


def revalidate_cases(tmp: Path) -> list:
    """revalidate over a ledger of completed entries, after a change on disk: the ledger, the journal and the line it prints."""
    cases = []

    def case(name, change):
        root = tmp / f"r{len(cases):02d}"
        root.mkdir()
        checks = [F.check_row("l.masters", "scopeprobe", exe="scopeprobe"),
                  dict(F.check_row("g.dep", "probe-json", exe="depprobe"), prerequisites=["l.masters"]),
                  dict(F.check_row("g.far", "probe-json", exe="depprobe"), prerequisites=["g.dep"]),
                  F.check_row("p.pool", "patchprobe", exe="patchprobe", arms=("baseline",))]
        plan = eval_plan(root, checks)
        with F.fake_clock():
            b = F.Builder(root, plan)
            for cid, arms in (("l.masters", ("baseline", "candidate-01")), ("g.dep", ("baseline", "candidate-01")),
                              ("g.far", ("candidate-01",)), ("p.pool", ("baseline",))):
                for aid in arms:
                    rel = f"outputs/{cid}/{aid}.json"
                    put(root, rel, json.dumps({"made": cid + aid}))
                    own = "A.esp" if (cid, aid) == ("l.masters", "baseline") else "B.esp" if cid != "p.pool" else "Target.esp"
                    b.settle(cid, aid, None, "completed", detector="no-finding", outputs=(rel,), consumed={
                        "configuration": {"plugins": [{"name": own, "path": str(root / "plugins" / own), "sha256": plugin_sha(own)}], "overlays": []},
                        "tool": {"present": False}})
            b.settle("g.far", "baseline", None, "rejected(exit-nonzero)", cause="exit-nonzero", accepted=False,
                     rejections=[{"cause": "exit-nonzero", "detail": "exit code 3"}])
            change(root)
            tree = snapshot(root)
        with F.fake_clock():   # a fresh counter: the C# side replays from here
            led = E.Ledger(root, plan)
            led.run_id = "run-2"
            with F.captured() as (out, _err):
                E.revalidate(root, plan, led, None)
        cases.append({"name": name, "tree": tree, "stdout": out.getvalue(),
                      "ledger": F.pack(F.mask((root / "ledger.json").read_text(encoding="utf-8"), root)),
                      "journal": F.pack(F.mask((root / "journal.jsonl").read_text(encoding="utf-8"), root))})

    def edit_receipt(root, key, fn):
        p = root / "receipts" / f"{key}.json"
        doc = json.loads(p.read_text(encoding="utf-8"))
        fn(doc)
        p.write_text(json.dumps(doc, indent=1, ensure_ascii=False), encoding="utf-8")

    case("nothing changed: every completed entry is reused", lambda root: None)
    case("the baseline's plugin changed: its entry and what rests on it", lambda root: put(root, "plugins/A.esp", "other bytes"))
    case("a receipt file is gone", lambda root: (root / "receipts" / "g.dep__candidate-01.json").unlink())
    case("a receipt of another plan", lambda root: edit_receipt(root, "p.pool__baseline", lambda d: d.__setitem__("planId", "planOTHER")))
    case("a receipt whose plugin has no path", lambda root: edit_receipt(
        root, "l.masters__candidate-01", lambda d: d["consumed"]["configuration"]["plugins"][0].pop("path")))
    case("an output edited after the run", lambda root: put(root, "outputs/g.dep/baseline.json", "{}"))
    return cases


# ------------------------------------------------------------------------------------------ the executor

def executor_cases(tmp: Path) -> dict:
    """account_stop, finish and consumed_for on one run folder: each step's receipt, ledger entry and journal line."""
    root = tmp / "exec"
    root.mkdir()
    checks = [dict(F.check_row("g.empty", "probe-emit", exe="contactprobe"), evidence={"strength": "measured"}),
              F.check_row("p.order", "derived")]
    plan = eval_plan(root, checks, budgets={"minutes": 0.5, "maxCandidates": 4, "candidatesHeldBack": []})
    put(root, "logs/g.empty__baseline.txt", "$ probe\n(exit 0, 1.0s)\n\n")
    put(root, "outputs/g.empty/baseline.json", '{"x": 1}')
    steps = []
    with F.fake_clock():
        F.Builder(root, plan)
        tree = snapshot(root)
    with F.fake_clock():   # a fresh counter: the C# side replays from here
        led = E.Ledger(root, plan)
        led.run_id = "run-2"
        ex = E.Executor(root, plan, led, resume=False)
        ex.parent_run_id = "parent-2"
        ex.preflight = {"controller": {"kind": "scripts", "digest": "scripts:" + "c" * 64},
                        "probe:contactprobe": {"kind": "package", "digest": "folder:" + "a" * 64}}
        check, base, cand = plan["checks"][0], ex.arms["baseline"], ex.arms["candidate-01"]
        log = root / "logs" / "g.empty__baseline.txt"

        journal_lines = [len((root / "journal.jsonl").read_text(encoding="utf-8").splitlines())]

        def record(name, key, call):
            before = journal_lines[0]
            journal_lines[0] = len((root / "journal.jsonl").read_text(encoding="utf-8").splitlines())
            steps.append(dict(call, name=name, key=key))
            steps[-1]["receipt"] = F.pack(F.mask((root / "receipts" / f"{key}.json").read_text(encoding="utf-8"), root))
            steps[-1]["entry"] = F.mask(led.doc["entries"][key], root)
            steps[-1]["journal"] = (root / "journal.jsonl").read_text(encoding="utf-8").splitlines()[before:]

        stops = [("a child the runner would not start", {"stopped": "before-launch"}, None, None),
                 ("a child that could not be contained", {"stopped": "uncontained", "refused": "AssignProcessToJobObject failed (5)", "runId": "child-1"}, "arm", True),
                 ("an uncontained child with no reason", {"stopped": "uncontained"}, "reverse", None),
                 ("a child stopped at the deadline, tree gone", {"stopped": "deadline", "seconds": 12.34, "treeStopped": True, "runId": "child-2"}, None, True),
                 ("a child stopped at the deadline, tree not confirmed, its process had ended",
                  {"stopped": "deadline", "seconds": 3, "treeStopped": False, "launchedExitCode": 4294967295}, "diff", None)]
        for name, res, part, with_log in stops:
            ex.account_stop(check, cand, part, res, "check g.empty [candidate-01]", log=log if with_log else None)
            record(name, E.Ledger.key("g.empty", "candidate-01", part), {"op": "stop", "arm": "candidate-01", "part": part, "res": res, "log": bool(with_log)})
        ex.account(check, base, "skipped-user", None, "not in --select")
        record("an accounting receipt", "g.empty__baseline", {"op": "account", "arm": "baseline", "status": "skipped-user", "cause": None, "detail": "not in --select"})
        ex.account(check, base, "skipped-user", None, "not in --select")
        record("the same account again keeps the receipt", "g.empty__baseline",
               {"op": "account", "arm": "baseline", "status": "skipped-user", "cause": None, "detail": "not in --select"})
        ex.account(check, base, "blocked(tool-missing)", None, "no build", extra={"switchScope": {"status": "x"}})
        record("another account replaces it", "g.empty__baseline",
               {"op": "account", "arm": "baseline", "status": "blocked(tool-missing)", "cause": None, "detail": "no build",
                "extra": {"switchScope": {"status": "x"}}})

        emit = root / "outputs" / "g.empty" / "baseline.json"
        argv = ["H:/no-harness/contactprobe.exe", "--order", base["candidatePath"], "--emit", str(emit), "--profile", "Default", "./rel", "plain"]
        identity = E.Validator.emit_identity(emit_doc(root, "contactprobe"))
        finishes = [
            ("an accepted result", {"accepted": True, "rejections": [], "detector": "no-finding", "findings": 0,
                                    "coverage": [{"Population": "p", "Judged": 2}], "verdict": None, "identity": identity, "identityLimits": []}, {}, {}),
            ("an accepted result with no detector", {"accepted": True, "rejections": [], "detector": None, "findings": None, "coverage": [],
                                                    "verdict": None, "identity": None, "identityLimits": [{"plugin": "A.esp"}]}, {"states": ["QuestDone"]}, {}),
            ("a rejected result", {"accepted": False, "rejections": [{"cause": "exit-nonzero", "detail": "exit code 3"}, {"cause": "output-missing", "detail": "x"}],
                                   "detector": None, "findings": None, "coverage": [], "verdict": None, "identity": None, "identityLimits": []}, {}, {}),
            ("a result with nothing accepted and nothing rejected", {"accepted": False, "rejections": [], "detector": None, "findings": None, "coverage": [],
                                                                    "verdict": None, "identity": None, "identityLimits": []}, {}, {}),
            ("an accepted result whose probe changed while it ran", {"accepted": True, "rejections": [], "detector": "finding", "findings": 2, "coverage": [],
                                                                    "verdict": None, "identity": identity, "identityLimits": []}, {},
             {"probe:contactprobe": "tool-changed: probe contactprobe is not the build this plan recorded, while check g.empty [baseline] ran"}),
        ]
        for name, v, extra, changed in finishes:
            ex.changed = dict(changed)
            ex.checked = ["launch", "acceptance"]
            ex.measured = {"probe:contactprobe": {"kind": "package", "digest": "folder:" + "b" * 64}} if changed else {}
            ex.finish(check, base, None, check["adapter"], argv, {"runId": "child-9", "exitCode": 0}, "2026-01-01T00:00:00Z",
                      "2026-01-01T00:00:09Z", [emit], log, v, extra=extra or None)
            record(name, "g.empty__baseline", {"op": "finish", "arm": "baseline", "v": F.mask(v, root), "extra": extra, "changed": changed,
                                               "argv": F.mask(argv, root)})
    consumed = []
    for name, aid, fn, identity in (
            ("the baseline, the scope measured on its candidate", "baseline", None, None),
            ("the baseline, the scope measured on another candidate", "baseline",
             lambda p: p["configuration"].__setitem__("source", "candidate:candidate-01-flip.json"), None),
            ("the baseline, a scope of the profile", "baseline", lambda p: p["configuration"].__setitem__("source", "profile:Default"), None),
            ("the baseline, a scope that states no plugins", "baseline", lambda p: p["configuration"].__setitem__("plugins", []), None),
            ("a candidate arm: its own request", "candidate-01", None, None),
            ("a candidate arm with a hash the plan could not state", "candidate-01", lambda p: unstated(p["arms"][::-1]), None),
            ("a candidate arm with no request", "candidate-01", lambda p: p["arms"][1].__setitem__("requested", {}), None),
            ("what the emit itself states", "candidate-01", None, {"fingerprint": "e" * 64, "plugins": []})):
        p2 = copy.deepcopy(plan)
        if fn:
            fn(p2)
        ex2 = E.Executor(root, p2, E.Ledger(root, p2), resume=False)
        arm = ex2.arms[aid]
        consumed.append({"name": name, "plan": packed_json(p2, root), "arm": aid, "identity": identity,
                         "want": F.mask(ex2.consumed_for(arm, check["adapter"], identity=identity), root)})
    return {"tree": tree, "steps": steps, "consumed": consumed, "budgetSentence": ex.budget_sentence()}


# ------------------------------------------------------------------------------------------ files Python cannot read

def crash_cases(tmp: Path) -> list:
    """What evalrun.py does with a run file of a shape its rules cannot read: a traceback and exit 1. The port refuses
    in one sentence, exit 2; these rows hold Python's side and the folder."""
    cases = []

    def case(name, argv, build):
        root = tmp / f"crash{len(cases)}"
        root.mkdir()
        build(root)
        tree = snapshot(root)
        env = dict(os.environ, HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"), PYTHONDONTWRITEBYTECODE="1")
        r = subprocess.run([sys.executable, str(F.HARNESS / "evalrun.py"), *argv, str(root)], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", env=env, cwd=str(root))
        cases.append({"name": name, "argv": argv, "tree": tree, "pythonExit": r.returncode, "pythonTraceback": "Traceback" in r.stderr,
                      "pythonLastLine": (r.stderr.strip().splitlines() or [""])[-1][:200]})

    def folder(root, plan_fix=None, ledger_fix=None):
        plan = eval_plan(root, [F.check_row("g.empty", "probe-emit", exe="contactprobe")])
        with F.fake_clock():
            b = F.Builder(root, plan)
            b.settle("g.empty", "baseline", None, "completed", detector="no-finding")
        if plan_fix:
            plan_fix(plan)
            body = F.jtext(plan)
            F.write(root, "plan.json", body)
            F.write(root, "plan.sha256", F.sha(body.encode("utf-8")) + "\n")
        if ledger_fix:
            doc = json.loads((root / "ledger.json").read_text(encoding="utf-8"))
            ledger_fix(doc)
            (root / "ledger.json").write_text(json.dumps(doc, indent=1), encoding="utf-8")

    case("status: a ledger entry with no checkId", ["status", "--run"],
         lambda root: folder(root, ledger_fix=lambda d: d["entries"]["g.empty__baseline"].pop("checkId")))
    case("status: a ledger whose entries are a list", ["status", "--run"], lambda root: folder(root, ledger_fix=lambda d: d.__setitem__("entries", [1])))
    case("run: a plan with no checks", ["run", "--run"], lambda root: folder(root, plan_fix=lambda p: p.pop("checks")))
    case("resume: a plan whose arms are text", ["resume", "--run"], lambda root: folder(root, plan_fix=lambda p: p.__setitem__("arms", "none")))
    case("finalize: a decision file that is a list", ["finalize", "--run"],
         lambda root: (folder(root), F.write(root, "decisions/d1.json", "[1, 2]")))
    return cases


SECTIONS = [("resolveSpec", resolve_cases), ("schedule", schedule_cases), ("states", states_cases), ("drift", drift_cases),
            ("validate", validate_cases), ("reuseIdentity", reuse_identity_cases), ("ownerDifferences", owner_difference_cases),
            ("generic", generic_cases), ("prerequisites", prereq_cases), ("reuse", reuse_cases), ("revalidate", revalidate_cases),
            ("executor", executor_cases), ("crash", crash_cases)]


def main() -> int:
    if os.name != "nt":
        print("make_known_answers_evaluate.py records Windows path rules; run it on Windows.", file=sys.stderr)
        return 2
    with Run("make-known-answers-evaluate", stage_source="single-stage"):
        return write_answers()


def write_answers() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-known-evaluate-")).resolve()
    os.environ["HOUSECARL_RUNLOG_DIR"] = str(tmp / "runlog")   # so no run folder here becomes the run-log store
    try:
        doc = {"python": platform.python_version(), "placeholders": {"backslash": F.BACK, "forward": F.FWD, "doubled": F.DOUBLED}}
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
