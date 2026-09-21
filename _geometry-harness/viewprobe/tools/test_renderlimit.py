"""How many renders may run side by side, and what happens when the machine will not say.

    python test_renderlimit.py

No GPU, no renderer and no game: every memory reading below is injected, so the four cases the
rule has to survive - plenty free, little free, no reader at all, a reader printing something that
is not a number - are decided here rather than on whatever hardware happens to run this.

The rule being checked: the side-by-side limit is COMPUTED from graphics memory free at launch,
bounded by system memory as well, from a per-process figure that is measured or supplied and never
a constant in the tree. And the part that makes it honest: when the machine cannot be asked, the
tool says so and does not guess - it falls to one process with the reason printed, or refuses.
Never silently to "as many as asked".

The one constant that IS policy rather than measurement is the reserve held back from free memory.
It is checked here for the three things that make a policy constant acceptable: it is visible in
the plan, a caller can override it, and the number is printed where a reader will see it.

The last two cases drive the side-by-side driver itself, so the rule is checked where it is used
and not only where it is written.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

FAILS = []


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


try:
    import renderlimit as RL
except ImportError as e:                       # the known-RED state: the helper does not exist yet
    RL = None
    IMPORT_ERROR = str(e)


def need(gpu=3000.0, sysm=4000.0, how="measured"):
    """What ONE process of this job needs. Never defaulted inside the helper - the caller supplies
    it or it is measured from a solo run of the same job."""
    return RL.Need(graphics_mib=gpu, system_mib=sysm, source=how)


def reading(resource, free, total=32768.0, ok=True, source="fake", reason=""):
    return RL.Reading(resource=resource, free_mib=free, total_mib=total,
                      source=source, ok=ok, reason=reason)


# ------------------------------------------------------------------ the four cases --

def test_plenty_free():
    p = RL.plan_limit(asked=4, need=need(),
                      graphics=reading(RL.GRAPHICS, 24000.0),
                      system=reading(RL.SYSTEM, 40000.0))
    check("plenty free: the caller's n is granted", p.limit == 4, repr(p))
    check("...and nothing is refused", p.refused is False, repr(p))
    check("...with the basis named", p.basis in ("asked", "requested"), repr(p.basis))
    check("...and a reason that carries the numbers",
          "24000" in p.reason or "24,000" in p.reason, repr(p.reason))


def test_little_free_cuts_the_number():
    p = RL.plan_limit(asked=4, need=need(),
                      graphics=reading(RL.GRAPHICS, 5000.0),
                      system=reading(RL.SYSTEM, 40000.0))
    check("little free: fewer than asked", p.limit < 4, repr(p))
    check("...but at least one", p.limit >= 1, repr(p))
    check("...bounded by graphics memory, and it says so", p.basis == RL.GRAPHICS, repr(p.basis))
    check("...with the per-process figure in the reason",
          "3000" in p.reason or "3,000" in p.reason, repr(p.reason))


def test_system_memory_bounds_it_too():
    """Stage 1 measured both running out together, so graphics alone is not the whole rule."""
    p = RL.plan_limit(asked=4, need=need(gpu=1000.0, sysm=6000.0),
                      graphics=reading(RL.GRAPHICS, 30000.0),
                      system=reading(RL.SYSTEM, 9000.0))
    check("a tight system memory cuts the number", p.limit < 4, repr(p))
    check("...and system memory is named as the bound", p.basis == RL.SYSTEM, repr(p.basis))


def test_not_even_one_fits_is_a_refusal():
    p = RL.plan_limit(asked=2, need=need(),
                      graphics=reading(RL.GRAPHICS, 900.0),
                      system=reading(RL.SYSTEM, 40000.0))
    check("when one process does not fit, the answer is a refusal", p.refused is True, repr(p))
    check("...and the limit is not quietly 1", p.limit == 0, repr(p.limit))
    check("...with a printable reason", isinstance(p.reason, str) and len(p.reason) > 20, repr(p.reason))


def test_reader_missing_never_grants_what_was_asked():
    p = RL.plan_limit(asked=4, need=need(),
                      graphics=reading(RL.GRAPHICS, None, None, ok=False,
                                       reason="nvidia-smi is not on PATH"),
                      system=reading(RL.SYSTEM, 40000.0))
    check("an unreadable graphics reading does not grant n", p.limit != 4, repr(p))
    check("...it falls to one process", p.limit == 1, repr(p))
    check("...saying which reading was missing", "nvidia-smi" in p.reason, repr(p.reason))
    check("...and marking the basis unreadable", p.basis == RL.UNREADABLE, repr(p.basis))


def test_reader_garbage_is_not_a_number():
    """A reader that answers 'N/A' has not answered. Parsing it as a number is the silent wrong
    answer this whole rule exists to prevent."""
    r = RL.read_graphics_free(run=lambda *a, **k: FakeProc(0, "N/A, N/A\n"),
                              which=lambda _n: "C:/fake/nvidia-smi.exe")
    check("garbage from the reader is not ok", r.ok is False, repr(r))
    check("...free is not invented", r.free_mib is None, repr(r.free_mib))
    check("...and the reason quotes what came back", "N/A" in r.reason, repr(r.reason))
    p = RL.plan_limit(asked=4, need=need(), graphics=r, system=reading(RL.SYSTEM, 40000.0))
    check("a garbage reading falls to one process", p.limit == 1, repr(p))
    check("...never to what was asked", p.limit != 4, repr(p))


def test_a_real_shaped_reading_parses():
    """The green control for the reader itself: a suite whose reader cannot read anything would
    pass every case above for the wrong reason."""
    r = RL.read_graphics_free(run=lambda *a, **k: FakeProc(0, "1234, 32768\n"),
                              which=lambda _n: "C:/fake/nvidia-smi.exe")
    check("a real-shaped answer is read", r.ok is True, repr(r))
    check("...free is total minus used", r.free_mib == 32768.0 - 1234.0, repr(r.free_mib))
    check("...and the source is named", "nvidia-smi" in r.source, repr(r.source))


def test_no_reader_at_all():
    r = RL.read_graphics_free(run=lambda *a, **k: FakeProc(0, ""), which=lambda _n: None)
    check("no reader on PATH is not ok", r.ok is False, repr(r))
    check("...and says what was looked for", "nvidia-smi" in r.reason, repr(r.reason))


def test_a_per_process_figure_is_never_assumed():
    """The tree carries no constant for what a render costs: it is measured from a solo run of the
    same job, or the caller supplies it."""
    try:
        p = RL.plan_limit(asked=4, need=None,
                          graphics=reading(RL.GRAPHICS, 24000.0),
                          system=reading(RL.SYSTEM, 40000.0))
    except (TypeError, ValueError) as e:
        check("no per-process figure is a refusal, not a default", True, type(e).__name__)
        return
    check("no per-process figure is a refusal, not a default", p.refused is True, repr(p))
    check("...and the reason says what is missing",
          "measure" in p.reason.lower() or "supplied" in p.reason.lower(), repr(p.reason))


def test_the_limit_never_exceeds_what_was_asked():
    p = RL.plan_limit(asked=2, need=need(gpu=10.0, sysm=10.0),
                      graphics=reading(RL.GRAPHICS, 30000.0),
                      system=reading(RL.SYSTEM, 60000.0))
    check("a machine with room to spare still runs only what was asked", p.limit == 2, repr(p))


def test_the_reserve_is_visible_and_overridable():
    """The reserve is a policy number, not a measured one. That is fine as long as nobody has to
    read the source to find out what was held back."""
    p = RL.plan_limit(asked=4, need=need(),
                      graphics=reading(RL.GRAPHICS, 24000.0),
                      system=reading(RL.SYSTEM, 40000.0))
    check("the default reserve is a named constant", isinstance(RL.RESERVE_MIB, (int, float)),
          repr(getattr(RL, "RESERVE_MIB", None)))
    check("...and the plan says how much was held back",
          str(int(RL.RESERVE_MIB)) in p.reason or str(int(RL.RESERVE_MIB)) in json_ish(p.detail),
          repr(p.reason))
    p2 = RL.plan_limit(asked=4, need=need(),
                       graphics=reading(RL.GRAPHICS, 12000.0),
                       system=reading(RL.SYSTEM, 40000.0), reserve_mib=9000.0)
    check("a caller can hold back more", p2.limit == 1, repr(p2))
    check("...and the plan says the number it used",
          "9000" in p2.reason or "9000" in json_ish(p2.detail), repr(p2.reason))


def json_ish(detail):
    try:
        return json.dumps(detail)
    except TypeError:
        return repr(detail)


def test_a_measured_need_is_labelled_as_measured():
    n = need(how="measured")
    p = RL.plan_limit(asked=4, need=n, graphics=reading(RL.GRAPHICS, 24000.0),
                      system=reading(RL.SYSTEM, 40000.0))
    check("the plan says where the per-process figure came from",
          "measured" in (p.detail.get("needSource", "") if isinstance(p.detail, dict) else ""),
          repr(p.detail))


# ------------------------------------------------- the driver that has to obey the rule --

AGREE = os.path.join(HERE, "renderagree.py")


LIMIT_LINE = re.compile(r"side-by-side limit:\s*(\d+)")


def dry_run(*flags):
    p = subprocess.run([sys.executable, AGREE, "--dry-run", "--exe", sys.executable,
                        "--work", os.path.join(HERE, "__nonexistent_work__"), *flags,
                        "--", "SomeCell"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    text = (p.stdout or "") + (p.stderr or "")
    m = LIMIT_LINE.search(text)
    return p.returncode, text, (int(m.group(1)) if m else None)


def test_driver_reports_the_limit_without_rendering():
    """`--dry-run` answers the question without starting a render, so what a batch WOULD do can be
    checked on any machine - including this one, where the readings are supplied rather than read."""
    rc, text, limit = dry_run("-n", "4", "--gpu-free-mib", "5000", "--sys-free-mib", "40000",
                              "--per-process-gpu-mib", "3000", "--per-process-sys-mib", "4000")
    check("the driver takes the dry run", "unrecognized arguments" not in text, text[-200:])
    check("...and exits 0 having rendered nothing", rc == 0, "exit %s" % rc)
    check("...printing the limit it computed", limit is not None, text[-200:])
    check("...which is the computed 1, not the asked 4", limit == 1, repr(limit))
    check("...and saying what it held back, so the reserve is not a number in the source only",
          "reserve" in text.lower(), text[-200:])


def test_driver_refuses_when_the_machine_cannot_be_asked():
    rc, text, limit = dry_run("-n", "4", "--per-process-gpu-mib", "3000",
                              "--per-process-sys-mib", "4000", "--no-memory-reader")
    check("the driver takes the no-reader case", "unrecognized arguments" not in text, text[-200:])
    check("an unreadable machine does not silently grant n", limit != 4, repr(limit))
    check("...it falls to one process", limit == 1, repr(limit))
    check("...and the reason is printed", "cannot" in text.lower() or "unreadable" in text.lower()
          or "not read" in text.lower(), text[-300:])
    check("...at an exit code a caller can act on", rc in (0, 2), "exit %s" % rc)


class FakeProc:
    def __init__(self, returncode, stdout):
        self.returncode, self.stdout, self.stderr = returncode, stdout, ""


def main():
    if RL is None:
        print("renderlimit is not importable: %s" % IMPORT_ERROR)
        print("  FAIL the side-by-side limit helper exists")
        FAILS.append("the side-by-side limit helper exists")
    cases = [test_driver_reports_the_limit_without_rendering,
             test_driver_refuses_when_the_machine_cannot_be_asked]
    if RL is not None:
        cases = [test_plenty_free, test_little_free_cuts_the_number,
                 test_system_memory_bounds_it_too, test_not_even_one_fits_is_a_refusal,
                 test_reader_missing_never_grants_what_was_asked,
                 test_reader_garbage_is_not_a_number, test_a_real_shaped_reading_parses,
                 test_no_reader_at_all, test_a_per_process_figure_is_never_assumed,
                 test_the_limit_never_exceeds_what_was_asked,
                 test_the_reserve_is_visible_and_overridable,
                 test_a_measured_need_is_labelled_as_measured] + cases
    for fn in cases:
        print(fn.__name__)
        try:
            fn()
        except AttributeError as e:
            check(fn.__name__ + ": the helper carries this", False, "AttributeError: " + str(e)[:90])
        except TypeError as e:
            check(fn.__name__ + ": reachable with these arguments", False, "TypeError: " + str(e)[:90])
    print("\n%d checks failed" % len(FAILS))
    for f in FAILS:
        print("  ", f)
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
