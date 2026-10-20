"""Known answers for ordersweep's refusal marker (REFUSED_RE): every refusal a probe can print is still caught, and a count
line that merely contains the word is not.

Dependency-free: `python _geometry-harness/tests/test_ordersweep_refused.py`.
The refusal strings are collected from source, not listed by hand: every C# string literal under _geometry-harness (bin and
obj excluded) that carries REFUSED or MISSING MASTER, and every message handed to PopulationRefused(...) or a configuration
Refuse(...). Interpolation holes are filled with a word, never a number, so a refusal sentence is not mistaken for a count.
The count lines are the console formats that print a number of refused items (overlapprobe's pair-loop timer line, the
contactprobe review summaries, the acceleration self-test), filled with numbers.
Each test names the wrong answer it rejects.
"""

import os
import re
import sys
import traceback
from pathlib import Path

HERE = Path(os.path.dirname(os.path.abspath(__file__)))
HARNESS = HERE.parent
sys.path.insert(0, str(HARNESS))

import ordersweep as O  # noqa: E402

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


def sources():
    for p in sorted(HARNESS.rglob("*.cs")):
        parts = set(p.parts)
        if "bin" in parts or "obj" in parts:
            continue
        yield p, p.read_text(encoding="utf-8", errors="replace")


# a C# string literal: optional $ and/or @, then "...", with \" escapes (regular) or "" escapes (verbatim)
LITERAL = re.compile(r'(\$@|@\$|\$|@)?"((?:[^"\\\n]|\\.|"")*)"')
HOLE = re.compile(r"\{[^{}]*\}")


def fill(text, word):
    return HOLE.sub(word, text.replace("\\n", "\n").replace('\\"', '"').replace('""', '"'))


def refusal_literals():
    """Every literal carrying an upper-case refusal marker, holes filled with a word."""
    out = []
    for p, src in sources():
        for m in LITERAL.finditer(src):
            body = m.group(2)
            if "REFUSED" in body or "MISSING MASTER" in body:
                out.append((f"{p.relative_to(HARNESS)}:{src.count(chr(10), 0, m.start()) + 1}", fill(body, "Name.esp")))
    return out


def keyed_refusals():
    """Lines that name one refused item and why (`{key} refused: {why}`), holes filled with a FormKey-like word."""
    out = []
    for p, src in sources():
        for m in LITERAL.finditer(src):
            if re.search(r"\} refused:", m.group(2)):
                out.append((f"{p.relative_to(HARNESS)}:{src.count(chr(10), 0, m.start()) + 1}", fill(m.group(2), "0A1B2C:Name.esp")))
    return out


def refusal_messages():
    """Every message handed to PopulationRefused(...) or a configuration Refuse(...): printed to stderr as the refusal."""
    out = []
    call = re.compile(r"\b(?:new PopulationRefused|Refuse)\(\s*((?:\$@|@\$|\$|@)?\"(?:[^\"\\\n]|\\.|\"\")*\")")
    for p, src in sources():
        for m in call.finditer(src):
            lit = LITERAL.match(m.group(1))
            if lit:
                out.append((f"{p.relative_to(HARNESS)}:{src.count(chr(10), 0, m.start()) + 1}", fill(lit.group(2), "Name.esp")))
    return out


def count_lines():
    """Console formats that print a NUMBER of refused things (holes filled with numbers)."""
    out = []
    for p, src in sources():
        for m in LITERAL.finditer(src):
            body = m.group(2)
            if re.search(r"refused \{|\} refused\b(?!:)", body):
                out.append((f"{p.relative_to(HARNESS)}:{src.count(chr(10), 0, m.start()) + 1}", fill(body, "1,234")))
    return out


TIMER_LINE = ("   pair-loop timers same: 537,452 pairs (223,553 mesh queries), 497.8s; pairframe 0.2s, prepare 2.9s, bvh 25.0s, "
              "surface 215.4s (11,483,854 u), validate 251.5s (18,371,285 u), containment 0.0s (3,898 u), unaccounted 2.7s; "
              "paths world 537,189 / zero-area skip 123 / shifted 90 / refused 50; budget-exhausted 0, time-outs 0, "
              "preparation refusals 0; served from the pair cache 313,887")


def test_every_refusal_literal_is_caught():
    """Wrong answer rejected: a narrowed marker that lets a real refusal pass as a clean run."""
    lits = refusal_literals() + refusal_messages() + keyed_refusals()
    assert len(lits) >= 60, f"collected only {len(lits)} refusal strings; the source scan is broken"
    missed = [(where, s) for where, s in lits
              if ("REFUSED" in s or "MISSING MASTER" in s or re.search(r"\brefused\b", s, re.I)) and not O.REFUSED_RE.search(s)]
    assert not missed, f"{len(missed)} refusal string(s) not caught, e.g. {missed[:3]}"


def test_population_and_configuration_refusals_with_the_word_are_caught():
    """Wrong answer rejected: a case-sensitive marker that drops a lower-case 'is refused.' refusal sentence."""
    worded = [(w, s) for w, s in refusal_messages() if re.search(r"\brefused\b", s, re.I)]
    assert worded, "no worded refusal message found; the source scan is broken"
    missed = [(w, s) for w, s in worded if not O.REFUSED_RE.search(s)]
    assert not missed, f"not caught: {missed[:3]}"


def test_count_lines_are_not_refusals():
    """Wrong answer rejected: a count of refused pairs (or reviews) read as the probe refusing the run."""
    lines = count_lines()
    assert len(lines) >= 5, f"collected only {len(lines)} count formats; the source scan is broken"
    hit = [(w, s) for w, s in lines if O.REFUSED_RE.search(s)]
    assert not hit, f"{len(hit)} count line(s) read as refusals, e.g. {hit[:2]}"


def test_overlapprobe_timer_line_is_not_a_refusal():
    """Wrong answer rejected: the real 2026-10-20 BPT candidate-01 timer line marking the job REFUSED."""
    assert not O.REFUSED_RE.search(TIMER_LINE)


def test_marker_forms_still_caught():
    """Wrong answer rejected: losing the marker shapes evalrun also recognises."""
    for s in ["   ! population gate: the arm REFUSED.", "REFUSED: a plugin's master is absent",
              "MISSING MASTER - 'Child.esp' masters 'Parent.esp', which this order does not load",
              "      the two configurations differ: Probe overlapprobe vs contactprobe. REFUSED.",
              "--population x.json names no plugin and no mod folder; an empty population would judge nothing, so it is refused."]:
        assert O.REFUSED_RE.search(s), s


if __name__ == "__main__":
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    sys.exit(1 if FAILURES else 0)
