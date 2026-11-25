"""Known answers for the measuring entry and the settings record the module's pipeline writes for a lab run
(evalpipeline.measuring_entry, evalpipeline.settings_record), once runcontrol's plan reads no settings file.

    python integration/housecarl/tests/test_measuring_entry.py

    M1  reverseComparison off reaches the measuring entry (evaluation.reverseComparison false) and the live check
        always reaches the overlay's opt-ins (overlay_opt_in), never the entry runcontrol plans from; every other key of the entry, its id, its relative paths and its
        presentation keys, is unchanged. Wrong answer: a fold that rewrites, reorders into new values or drops a key.
    M2  default settings leave the measuring entry equal to the entry, so a run folder measured before is reused.
    M3  a model setting changes the run's settings record and not the measuring entry. Wrong answer: the model choice
        recorded nowhere (I1 to I8 hold that runcontrol's plan no longer records it; this holds that the module does).
    M4  a measuring choice the entry states itself wins over the settings.
    M5  the live check set to ask folds nothing and says so in one sentence.
"""
from __future__ import annotations

import copy
import contextlib
import io
import json
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import evalpipeline as EP  # noqa: E402
import evalsettings as ES  # noqa: E402

FAILURES: list[str] = []
LIVE = "runtime.state-switch-flip"
ENTRY = {"schema": 1, "id": "lab-alpha", "subject": {"plugin": "Alpha.esp", "mod": "Alpha"},
         "source": {"instance": "S:/src", "profile": "P"}, "lab": {"instance": "L:/lab", "game": "G:/game"},
         "evaluation": {"depth": "deep", "budgetMinutes": 30, "optIn": [], "primitiveSolidity": "tables/solid.json"},
         "modNames": {"Alpha.esp": "Alpha"}, "patchSearch": "searches/alpha.json", "report": {"mode": "concise"}}


def check(name, ok, why):
    print(f"  {'PASS' if ok else 'FAIL'}  {name}: {why}")
    if not ok:
        FAILURES.append(name)


def settings(doc: dict | None):
    """The user's evaluation settings as the module loads them, from a file of this test's own."""
    t = Path(tempfile.mkdtemp(prefix="hc-measuring-"))
    p = t / "houseCARL.evaluation.json"
    if doc is not None:
        p.write_text(json.dumps({"schemaVersion": 1, **doc}), encoding="utf-8")
    return ES.load(str(p))


def missing(*names):
    gone = [n for n in names if not callable(getattr(EP, n, None))]
    return f"the module's pipeline has no {', '.join(gone)}: a lab run's settings reach no measuring entry and are recorded nowhere by the module" if gone else None


def main() -> int:
    why = missing("measuring_entry", "settings_record", "overlay_opt_in")
    if why:
        for n in ("M1", "M2", "M3", "M4", "M5"):
            check(n, False, why)
        print(f"\n{len(FAILURES)} failed")
        return 1
    folded, said = EP.measuring_entry(copy.deepcopy(ENTRY), settings({"evaluation": {"reverseComparison": False, "liveSwitchCheck": "always"},
                                                                       "liveSwitchCheck": {"instance": "X:/i", "shortcut": "X:/s.lnk", "gameExe": "X:/g.exe"}}))
    want = copy.deepcopy(ENTRY)
    want["evaluation"]["reverseComparison"] = False
    others_same = all(folded.get(k) == ENTRY.get(k) for k in ENTRY if k != "evaluation") and set(folded) == set(ENTRY)
    ev_same = all(folded["evaluation"].get(k) == ENTRY["evaluation"].get(k) for k in ENTRY["evaluation"] if k != "optIn")
    ai = EP.overlay_opt_in(copy.deepcopy(ENTRY), settings({"evaluation": {"liveSwitchCheck": "always"},
                                                        "liveSwitchCheck": {"instance": "X:/i", "shortcut": "X:/s.lnk", "gameExe": "X:/g.exe"}}))
    check("M1", folded == want and others_same and ev_same and list(folded) == list(ENTRY) and ai == [LIVE],
          f"folded {json.dumps(folded)[:300]}")
    plain, _ = EP.measuring_entry(copy.deepcopy(ENTRY), settings(None))
    plain2, _ = EP.measuring_entry(copy.deepcopy(ENTRY), settings({"evaluation": {"reverseComparison": True, "liveSwitchCheck": "off"}}))
    check("M2", json.dumps(plain) == json.dumps(ENTRY) and json.dumps(plain2) == json.dumps(ENTRY), "default settings leave the entry as it is")
    m1, _ = EP.measuring_entry(copy.deepcopy(ENTRY), settings({"models": {"rewriteExplanations": "off"}}))
    m2, _ = EP.measuring_entry(copy.deepcopy(ENTRY), settings({"models": {"rewriteExplanations": "session"}}))
    r1 = EP.settings_record(settings({"models": {"rewriteExplanations": "off"}}))
    r2 = EP.settings_record(settings({"models": {"rewriteExplanations": "session"}}))
    o1 = ((r1.get("options") or {}).get("models.rewriteExplanations") or {}).get("value")
    o2 = ((r2.get("options") or {}).get("models.rewriteExplanations") or {}).get("value")
    check("M3", json.dumps(m1) == json.dumps(m2) and r1 != r2 and (o1, o2) == ("off", "session") and r1.get("sha256") != r2.get("sha256"),
          f"measuring entries equal {json.dumps(m1) == json.dumps(m2)}; the record's model values {o1}, {o2}")
    own = copy.deepcopy(ENTRY)
    own["evaluation"]["reverseComparison"] = True
    kept, _ = EP.measuring_entry(copy.deepcopy(own), settings({"evaluation": {"reverseComparison": False}}))
    check("M4", kept["evaluation"].get("reverseComparison") is True, f"the entry's own reverseComparison true, the settings false: {kept['evaluation']}")
    asked, said = EP.measuring_entry(copy.deepcopy(ENTRY), settings({"evaluation": {"liveSwitchCheck": "ask"}}))
    check("M5", json.dumps(asked) == json.dumps(ENTRY) and len(said) == 1 and "--opt-in" in said[0] and "\n" not in said[0],
          f"said {said}")
    print(f"\n{'all checks passed' if not FAILURES else f'{len(FAILURES)} failed'}")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
