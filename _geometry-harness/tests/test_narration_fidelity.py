"""Known answers: the narration packet asks for plain words and carries the glossary, and narrate-apply accepts a
rewrite only when a fidelity judgement bound to its exact text says it keeps the meaning (C2020-C2029).

    python tests/test_narration_fidelity.py [--only C2022,C2023]

No game, no model call. Each case runs the real `evalreport.py` (HOUSECARL_EVALREPORT names another tree's; default this
checkout's) on a hand-built presentation, and reads the SHIPPED skill data
(`evaluation-reporting/references/narration.json`), so a skill-data change counts. The plugins, arm and counts are made
up; nothing here is evidence about a real load order. The judgements are hand-written stand-ins for the judge agent's
answer: these cases test the gate that reads them, not the judge.

    C2020  the packet's instructions ask for plain words: they carry the skill's plain-words sentence, which names the
           tool's own words to translate (wrong answer: the 4.14 instructions, which only say "plain language")
    C2021  the packet carries the skill's glossary, every term with its plain meaning, and the instructions point at it
           (wrong answer: a packet with no glossary, so a narrator cannot explain a term in place)
    C2022  narrate-apply with no judgements refuses the run in one sentence and changes nothing (wrong answer: a
           lexically valid wrong rewrite - the regression told as a fix - is accepted, as on 3b1ad8e0)
    C2023  a wrong rewrite judged unfaithful is refused `judged-unfaithful` and keeps the template sentence
    C2024  a right rewrite judged faithful is accepted and becomes the finding's sentence
    C2025  a judgement is bound to the exact text: one made for another text is `judgement-stale`, an answer with no
           judgement is `unjudged`, and a judgements file for another presentation refuses the run (`judgements-foreign`)
    C2026  a faithful judgement never overrides a lexical refusal (an unbound number stays `unbound-number`)
    C2027  `narrate-packet --answers` writes the judge's packet: each answer's text and its sha256, the finding's
           template and counts, the glossary, and the skill's judge instructions
    C2028  the judge runs in its own model setting, `models.checkExplanations` (default: the session's model; never off),
           which never resolves to a Fable model: a Fable session is routed to the Opus agent and says so, and route
           without --session-model refuses in one sentence (wrong answer: the judge runs on whatever the session is)
    C2029  the presentation contract and the reporting skill say that a rewrite needs a faithful judgement
    C2050  the judge's packet carries each answer's bound rows, every one (form key, cell, summary, physical detail),
           not only the rows the report shows (wrong answer: template and counts only, so a plugin's role or a detail
           only the rows give cannot be checked: a right rewrite citing a row is called unsupported, a wrong one
           telling the objects as placed by the plugin pair is accepted)
    C2051  the judge's instructions read the template with its `rows`: detail a row gives is supported, and a role or
           detail the rows contradict is distorted (wrong answer: rows in the packet that the instructions never use)
    C2052  the narrator's instructions forbid claiming the evaluation is complete or the mods compatible, and keep a
           check's own completion as a fact (wrong answer: "never claim completion", which contradicts a template
           saying a check ran to completion)
    C2053  the presentation contract says the judge's packet carries the finding's bound rows

Each case names the wrong answer it exists to reject. A case whose command does not exist in the tree under test (an
argparse usage error) is RED with "usage error" in its line, never GREEN. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
EVALREPORT = Path(os.environ.get("HOUSECARL_EVALREPORT") or (HARNESS / "evalreport.py"))
TREE = EVALREPORT.parent.parent
REFS = TREE / ".claude" / "skills" / "evaluation-reporting" / "references"
DATA = REFS / "narration.json"
os.environ["PYTHONUTF8"] = "1"
os.environ.setdefault("HOUSECARL_EVAL_SETTINGS",
                      str(Path(tempfile.gettempdir()) / "hc-tests-no-eval-settings" / "houseCARL.evaluation.json"))

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []

ARM = "candidate-01-both"
TEMPLATE = ("On arm candidate-01-both, contactprobe reports 3 record(s) that entered the CONTACT class with the physical "
            "predicate holding, 3 of them a new defect; the same probe reports 0 repaired and 0 reclassified record(s), "
            "which do not net against the new defect. Its verdict line reads DIFFERENTIAL-HARMFUL. The arm's "
            "classification is REGRESSION.")
WRONG = ("With Alpha.esp and Beta.esp loaded together (setup candidate-01-both), the placement check found 3 objects that "
         "had been left floating and are now fixed, so this setup makes object placement better.")
RIGHT = ("With Alpha.esp and Beta.esp loaded together (setup candidate-01-both), the placement check found 3 objects newly "
         "left touching nothing because what they rested on was moved; all 3 are new problems, and 0 were repaired or "
         "changed owner, so nothing offsets them. The comparison rates this setup worse than without the mods: it makes "
         "things worse.")
UNBOUND = RIGHT.replace("found 3 objects", "found 7 objects")
SECOND = "On arm candidate-01-both, 2 notes."


def presentation() -> dict:
    f1 = {"id": "F-001", "kind": "regression", "checkId": "geometry.placement-contact", "armId": ARM, "part": "diff",
          "title": "Objects left touching nothing", "plain": TEMPLATE,
          "counts": {"probe": "contactprobe", "classification": "REGRESSION", "verdict": "DIFFERENTIAL-HARMFUL",
                     "A": "Alpha.esp", "B": "Beta.esp", "created": 3, "newDefect": 3, "repaired": 0, "reclassified": 0}}
    f2 = {"id": "F-002", "kind": "catalogue", "checkId": "geometry.placement-contact", "armId": ARM, "part": "arm",
          "title": "Notes", "plain": SECOND, "counts": {"probe": "contactprobe", "rows": 2}}
    return {"schemaVersion": 1, "presentationId": "000000000000", "findings": [f1, f2], "settings": {"rowsPerGroup": 3},
            "source": {}, "evidence": [], "imports": {}, "chapters": [],
            "scope": {"arms": [{"armId": "baseline", "readerLabel": "without the mods", "pluginsAdded": []},
                               {"armId": ARM, "readerLabel": "with both mods", "pluginsAdded": ["Alpha.esp", "Beta.esp"]}],
                      "checks": {"geometry.placement-contact": {}}, "targets": []},
            "reader": {"sectionHeadings": {}, "prediction": {}, "coverage": {}, "validation": {}}}


def sha(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    if cond:
        RESULTS.append((case, "GREEN", ""))
    else:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    return cond


def run(args: list, tmp: Path) -> subprocess.CompletedProcess:
    env = {**os.environ, "HOUSECARL_RUNLOG_DIR": str(tmp / "runlog"), "PYTHONIOENCODING": "utf-8"}
    return subprocess.run([sys.executable, str(EVALREPORT), *map(str, args)], capture_output=True, text=True,
                          encoding="utf-8", env=env, timeout=300)


def usage_error(r: subprocess.CompletedProcess) -> bool:
    return r.returncode == 2 and ("unrecognized arguments" in r.stderr or "invalid choice" in r.stderr)


def jload(p: Path):
    try:
        return json.loads(Path(p).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def data() -> dict:
    return jload(DATA) or {}


def setup(tmp: Path, name: str, answers: dict, judgements=None, pres=None) -> tuple[Path, Path, Path | None]:
    d = tmp / name
    d.mkdir(parents=True, exist_ok=True)
    p = d / "presentation.json"
    plan = pres or presentation()
    p.write_text(json.dumps(plan, indent=1), encoding="utf-8")
    a = d / "answers.json"
    a.write_text(json.dumps({"presentationId": plan["presentationId"],
                             "answers": [{"id": k, "text": v} for k, v in answers.items()]}, indent=1), encoding="utf-8")
    j = None
    if judgements is not None:
        j = d / "judgements.json"
        j.write_text(json.dumps(judgements, indent=1), encoding="utf-8")
    return p, a, j


def judged(entries: list, pid="000000000000") -> dict:
    return {"presentationId": pid, "judgements": [
        {"id": i, "textSha256": sha(t), "faithful": ok, "facts": [], "added": [] if ok else ["told as fixed"],
         "reason": "stand-in judgement"} for i, t, ok in entries]}


def apply(tmp: Path, name: str, answers: dict, judgements=None, pres=None):
    p, a, j = setup(tmp, name, answers, judgements, pres)
    args = ["narrate-apply", "--presentation", p, "--answers", a] + (["--judgements", j] if j else [])
    r = run(args, tmp)
    after = jload(p) or {}
    plains = {f["id"]: f.get("plain") for f in after.get("findings") or []}
    refused = (after.get("narration") or {}).get("refused") or []
    rules = {x.get("id"): x.get("rule") for x in refused if isinstance(x, dict)}
    return r, plains, rules


def packet(tmp: Path, name: str, extra=()) -> tuple[subprocess.CompletedProcess, dict]:
    p, a, _ = setup(tmp, name, {"F-001": RIGHT})
    out = tmp / name / "packet.json"
    r = run(["narrate-packet", "--presentation", p, "--out", out, *[x if x != "@answers" else a for x in extra]], tmp)
    return r, jload(out) or {}


def c2020(tmp):
    r, pk = packet(tmp, "c2020")
    want = (data().get("narrator") or {}).get("plainWords")
    ins = pk.get("instructions") or ""
    check("C2020", r.returncode == 0 and bool(want) and want in ins
          and all(w in ins for w in ("never used", "probe", "arm", "record")),
          "the packet's instructions do not carry the skill's plain-words sentence naming the tool's words",
          f"exit {r.returncode}; skill data {'present' if want else 'absent'}; instructions: {ins[:120]!r}")


def c2021(tmp):
    r, pk = packet(tmp, "c2021")
    want = data().get("glossary")
    got = pk.get("glossary")
    ok = (r.returncode == 0 and isinstance(want, list) and len(want) >= 20 and got == want
          and all(isinstance(g, dict) and g.get("term") and g.get("plain") for g in got)
          and "glossary" in (pk.get("instructions") or ""))
    check("C2021", ok, "the packet does not carry the skill's glossary (every term with its plain meaning)",
          f"exit {r.returncode}; skill glossary {len(want) if isinstance(want, list) else 'absent'}; packet glossary "
          f"{len(got) if isinstance(got, list) else 'absent'}")


def c2022(tmp):
    r, plains, rules = apply(tmp, "c2022", {"F-001": WRONG})
    one_line = r.stderr.strip().count("\n") == 0 and "judgement" in r.stderr.lower()
    check("C2022", r.returncode != 0 and plains.get("F-001") == TEMPLATE and one_line,
          "a lexically valid wrong rewrite is accepted with no fidelity judgement",
          f"exit {r.returncode}; F-001 is {'the template' if plains.get('F-001') == TEMPLATE else 'the rewrite'}; "
          f"stderr {r.stderr.strip()[:160]!r}")


def c2023(tmp):
    r, plains, rules = apply(tmp, "c2023", {"F-001": WRONG}, judged([("F-001", WRONG, False)]))
    if usage_error(r):
        return check("C2023", False, "usage error: narrate-apply has no --judgements", r.stderr.strip()[-120:])
    check("C2023", r.returncode == 0 and plains.get("F-001") == TEMPLATE and rules.get("F-001") == "judged-unfaithful",
          "a wrong rewrite judged unfaithful is not refused as judged-unfaithful",
          f"exit {r.returncode}; rule {rules.get('F-001')!r}")


def c2024(tmp):
    r, plains, rules = apply(tmp, "c2024", {"F-001": RIGHT}, judged([("F-001", RIGHT, True)]))
    if usage_error(r):
        return check("C2024", False, "usage error: narrate-apply has no --judgements", r.stderr.strip()[-120:])
    check("C2024", r.returncode == 0 and plains.get("F-001") == RIGHT and not rules,
          "a right rewrite judged faithful is not accepted", f"exit {r.returncode}; refused {rules}")


def c2025(tmp):
    r, plains, rules = apply(tmp, "c2025", {"F-001": RIGHT, "F-002": "On the setup candidate-01-both, 2 notes."},
                             judged([("F-001", WRONG, True)]))
    if usage_error(r):
        return check("C2025", False, "usage error: narrate-apply has no --judgements", r.stderr.strip()[-120:])
    r2, plains2, _ = apply(tmp, "c2025-foreign", {"F-001": RIGHT}, judged([("F-001", RIGHT, True)], pid="ffffffffffff"))
    check("C2025", r.returncode == 0 and rules.get("F-001") == "judgement-stale" and rules.get("F-002") == "unjudged"
          and plains.get("F-001") == TEMPLATE and r2.returncode != 0 and "judgements-foreign" in r2.stderr
          and plains2.get("F-001") == TEMPLATE,
          "a judgement is not bound to its answer's exact text and presentation",
          f"rules {rules}; foreign exit {r2.returncode} {r2.stderr.strip()[:100]!r}")


def c2026(tmp):
    r, plains, rules = apply(tmp, "c2026", {"F-001": UNBOUND}, judged([("F-001", UNBOUND, True)]))
    if usage_error(r):
        return check("C2026", False, "usage error: narrate-apply has no --judgements", r.stderr.strip()[-120:])
    check("C2026", r.returncode == 0 and rules.get("F-001") == "unbound-number" and plains.get("F-001") == TEMPLATE,
          "a faithful judgement overrides a lexical refusal", f"rule {rules.get('F-001')!r}")


def c2027(tmp):
    r, pk = packet(tmp, "c2027", ["--answers", "@answers"])
    if usage_error(r):
        return check("C2027", False, "usage error: narrate-packet has no --answers", r.stderr.strip()[-120:])
    items = pk.get("answers") or []
    one = next((x for x in items if x.get("id") == "F-001"), {})
    want = (data().get("judge") or {}).get("instructions")
    check("C2027", r.returncode == 0 and one.get("text") == RIGHT and one.get("textSha256") == sha(RIGHT)
          and one.get("template") == TEMPLATE and (one.get("counts") or {}).get("created") == 3
          and pk.get("glossary") == data().get("glossary") and bool(want) and pk.get("instructions") == want,
          "the judge's packet does not carry the answer, its hash, the finding, the glossary and the judge instructions",
          f"exit {r.returncode}; keys {sorted(pk)}")


def c2028(tmp):
    sys.path.insert(0, str(EVALREPORT.parent))
    try:
        import evalsettings as ES
        o = ES.BY_KEY.get("models.checkExplanations")
    finally:
        sys.path.pop(0)
    es = EVALREPORT.parent / "evalsettings.py"
    env = {**os.environ, "CLAUDECODE": "1", "HOUSECARL_RUNLOG_DIR": str(tmp / "runlog"), "PYTHONIOENCODING": "utf-8"}

    def route(*extra):
        r = subprocess.run([sys.executable, str(es), "route", "models.checkExplanations", *extra], capture_output=True,
                           text=True, encoding="utf-8", env=env, timeout=120)
        try:
            return r, json.loads(r.stdout.strip().splitlines()[-1])
        except (ValueError, IndexError):
            return r, {}
    fable, fo = route("--session-model", "claude-fable-1")
    opus, oo = route("--session-model", "claude-opus-5-5")
    bare, _ = route()
    ok = (bool(o) and o.get("type") == "model" and o.get("default") == "session" and not o.get("allowOff")
          and fable.returncode == 0 and fo.get("agent", "").endswith("model-claude-opus-5-5")
          and "claude-opus-5-5" in fable.stderr and "fable" not in fo.get("agent", "").lower()
          and opus.returncode == 0 and oo.get("use") == "session"
          and bare.returncode != 0 and "--session-model" in bare.stderr and bare.stderr.strip().count("\n") == 0)
    check("C2028", ok, "the fidelity judge has no model setting of its own, or can resolve to a Fable session",
          f"option {bool(o)}; fable -> {fo or fable.stderr.strip()[-120:]}; opus -> {oo}; bare exit {bare.returncode} "
          f"{bare.stderr.strip()[-120:]!r}")


def c2029(tmp):
    std = (TREE / "standards" / "HOUSECARL_EVALUATION_PRESENTATION.md").read_text(encoding="utf-8")
    skill = (TREE / ".claude" / "skills" / "evaluation-reporting" / "SKILL.md").read_text(encoding="utf-8")
    rules = ("judged-unfaithful", "judgement-stale", "unjudged", "judgements-foreign")
    check("C2029", all(x in std for x in rules) and "models.checkExplanations" in skill and "--judgements" in skill,
          "the contract or the skill does not say a rewrite needs a faithful judgement",
          f"standard has {[x for x in rules if x in std]}; skill names the setting: {'models.checkExplanations' in skill}")


ROWS = [{"formKey": f"00000{i}:Alpha.esp", "label": f"AlphaCell0{i} (int)",
         "summary": f"Stranded: Furniture Bed0{i} Supported on Static Floor0{i} which Beta.esp Removed; nearest thing now 128u "
                    f"00000{i}:Alpha.esp / 00001{i}:Alpha.esp in AlphaCell0{i} (int)",
         "physical": "Holds", "physicalDetail": "touches nothing within 4u (nearest 128u)", "evidenceId": "E-0"}
        for i in range(1, 6)]


def presentation_rows() -> dict:
    """F-001 with five bound rows, three of them shown (rowsPerGroup 3), as plan writes `rows` and `allRows`."""
    plan = presentation()
    f1 = plan["findings"][0]
    f1["allRows"], f1["rows"], f1["rowsTotal"], f1["rowsShown"] = ROWS, ROWS[:3], 5, 3
    return plan


def c2050(tmp):
    p, a, _ = setup(tmp, "c2050", {"F-001": RIGHT, "F-002": SECOND + " Two notes."}, pres=presentation_rows())
    out = tmp / "c2050" / "packet.json"
    r = run(["narrate-packet", "--presentation", p, "--answers", a, "--out", out], tmp)
    if usage_error(r):
        return check("C2050", False, "usage error: narrate-packet has no --answers", r.stderr.strip()[-120:])
    items = {x.get("id"): x for x in (jload(out) or {}).get("answers") or []}
    got = (items.get("F-001") or {}).get("rows")
    keys = ("formKey", "label", "summary", "physicalDetail")
    ok_rows = isinstance(got, list) and [{k: x.get(k) for k in keys} for x in got] == [{k: x[k] for k in keys} for x in ROWS]
    ok_none = (items.get("F-002") or {}).get("rows") == []
    check("C2050", r.returncode == 0 and ok_rows and ok_none,
          "the judge's packet does not carry every bound row of each answer's finding",
          f"exit {r.returncode}; F-001 rows {len(got) if isinstance(got, list) else 'absent'} of {len(ROWS)}; "
          f"F-002 rows {(items.get('F-002') or {}).get('rows', 'absent')!r}")


def c2051(tmp):
    ins = (data().get("judge") or {}).get("instructions") or ""
    supported = re.search(r"taken correctly from `counts` or `rows`", ins)
    contradicts = re.search(r"the rows contradict", ins)
    check("C2051", bool(supported and contradicts and "`rows`" in ins.split("Give each fact")[0]),
          "the judge's instructions do not read the template with its rows (support and contradiction)",
          f"supported-by-rows {bool(supported)}; contradicted-by-rows {bool(contradicts)}")


def c2052(tmp):
    r, pk = packet(tmp, "c2052")
    ins = pk.get("instructions") or ""
    check("C2052", r.returncode == 0 and "never claim completion" not in ins
          and "the evaluation is complete" in ins and "ran to completion" in ins and "keep that fact" in ins,
          "the narrator's instructions ban every completion claim, against a template saying a check ran to completion",
          f"exit {r.returncode}; blanket ban {'present' if 'never claim completion' in ins else 'absent'}")


def c2053(tmp):
    std = (TREE / "standards" / "HOUSECARL_EVALUATION_PRESENTATION.md").read_text(encoding="utf-8")
    para = next((x for x in std.split("\n\n") if "narrate-packet --presentation P --answers" in x), "")
    check("C2053", "bound rows" in para and "`rows`" in para,
          "the contract does not say the judge's packet carries the finding's bound rows",
          f"judge paragraph found {bool(para)}")


CASES = {"C2020": c2020, "C2021": c2021, "C2022": c2022, "C2023": c2023, "C2024": c2024, "C2025": c2025,
         "C2026": c2026, "C2027": c2027, "C2028": c2028, "C2029": c2029,
         "C2050": c2050, "C2051": c2051, "C2052": c2052, "C2053": c2053}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    a = ap.parse_args()
    want = set(filter(None, a.only.split(",")))
    tmp = Path(tempfile.mkdtemp(prefix="hc-narration-fidelity-"))
    for cid, fn in CASES.items():
        if want and cid not in want:
            continue
        try:
            fn(tmp)
        except Exception as e:                       # a crash is a failure, never a skip
            FAILS.append(cid)
            RESULTS.append((cid, "CRASH", f"{type(e).__name__}: {e}"))
            traceback.print_exc()
    for cid, verdict, why in RESULTS:
        print(f"{verdict:5} {cid}" + (f"  {why}" if why else ""))
    print(f"{len(FAILS)} failing check(s)" if FAILS else "all cases GREEN")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
