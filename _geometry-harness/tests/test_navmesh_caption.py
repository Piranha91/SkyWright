"""Known answers for the navmesh context caption: which earlier version it names, and how (C2240-C2244).

    python tests/test_navmesh_caption.py [--only C2240,C2243]

No game and no renderer. The reel cases call the real reel_plan.plan_reel on test_navmesh_reel.py's planner fixture
with the SHIPPED skill data; the report case formats the shipped caption words with the parser evalreport's caption
path calls. A function the fix adds is looked up with getattr, so HEAD fails the case's own assertion, never by a crash.
The earlier mod is named "Interesting NPCs", a name ending in "s" (Moon and Star's reel-02 read "Interesting NPCs's").

    C2240  the historical frame's reel label names the drawn mod, says it is the version the winner replaced, and has
           no "s's" possessive
    C2241  the label names the plugin the frame's own --nav-version draws, not the plan's re-cut history
    C2242  a historical frame without --nav-version gets a label that names no plugin
    C2243  the report caption names the frame's --nav-version plugin through reel_plan's one parser, with no "s's"
    C2244  the navmesh narration has no "s's" possessive and still names the earlier mod

Each case names the wrong answer it exists to reject. Exit 0: every case passed; 1: a case failed.
"""
from __future__ import annotations

import argparse
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))
os.environ["PYTHONUTF8"] = "1"

import test_navmesh_reel as NR  # noqa: E402  (the planner fixture and shipped skill data, reused, not copied)

FAILS: list[str] = []
RESULTS: list[tuple[str, str, str]] = []
EARLIER_NAME, THIRD_NAME = "Interesting NPCs", "Third Mod"
BAD = "s's"


def check(case: str, cond: bool, wrong: str, detail: str = "") -> bool:
    """`wrong` names the wrong answer the assertion rejects; it is what a red case prints."""
    if not cond:
        FAILS.append(case)
        RESULTS.append((case, "RED", wrong + (f" -- {detail}" if detail else "")))
    else:
        RESULTS.append((case, "GREEN", ""))
    return cond


def vignette(argv=None):
    """The navmesh vignette planned from the fixture, the historical frame's renderer command set to `argv`."""
    import reel_plan as RP
    pol, pri = NR.shipped()
    pres, names, masters, search, mods, captures = NR.planner_fixture()
    mods[NR.EARLIER]["name"] = EARLIER_NAME
    mods[NR.OTHER] = {"name": THIRD_NAME, "source": "nexus-catalogue"}
    for m in pres["media"]:
        if m["provenance"]["vignetteState"] == NR.STATES[1] and argv is not None:
            m["provenance"]["argv"] = argv
    reel = RP.plan_reel(pres, names, masters, pol["vignettes"], pri["reel"]["words"], search, mods, captures,
                        captures_final=True)
    chs = [ch for ch in NR.nav_chapters(reel) if ch.get("kind") == "vignette"]
    return (chs[0] if chs else None), pri


def label(ch):
    fr = (ch or {}).get("frames") or []
    return str(fr[1].get("label")) if len(fr) == 2 else ""


def c2240_label_names_drawn_and_relation(_tmp):
    ch, _ = vignette(["--nav", "both", "--nav-version", f"{NR.SRC}={NR.EARLIER}"])
    lb = label(ch)
    check("C2240", bool(ch) and EARLIER_NAME in lb and "Later Mod" in lb and "replaced" in lb and BAD not in lb,
          "the historical frame's label omits the drawn mod or the winner it was replaced by, or says \"s's\"", lb)


def c2241_label_reads_the_frame(_tmp):
    ch, _ = vignette(["--nav", "both", "--nav-version", f"{NR.SRC}={NR.OTHER}"])
    lb = label(ch)
    check("C2241", bool(ch) and THIRD_NAME in lb and EARLIER_NAME not in lb,
          "the label names the plan's re-cut history, not the plugin the frame's --nav-version draws", lb)


def c2242_label_without_version(_tmp):
    ch, _ = vignette(["--nav", "both"])
    lb = label(ch)
    check("C2242", bool(ch) and bool(lb) and lb != "None" and not any(n in lb for n in (EARLIER_NAME, THIRD_NAME, "Later Mod")),
          "a historical frame with no --nav-version gets a label that names a plugin, or none at all", lb)


def c2243_report_caption(_tmp):
    import evalreport as ER
    import reel_plan as RP
    _pol, pri = NR.shipped()
    parse = getattr(RP, "navmesh_earlier_plugin", None)
    media = [{"provenance": {"argv": ["--nav", "both", "--red", f"{NR.SRC}:7"]}},
             {"provenance": {"argv": ["--nav", "both", "--nav-version", f"{NR.SRC}=3DNPC.esp"]}}]
    got = [parse(media) if parse else None, ER.navmesh_earlier_plugin(media)]
    check("C2243", got == ["3DNPC.esp", "3DNPC.esp"],
          "reel_plan has no navmesh_earlier_plugin, or evalreport's caption path parses the frame differently", str(got))
    words = str(pri["reader"]["figureCaptions"]["historical-navmesh-context"]).format(earlier=EARLIER_NAME)
    check("C2243", EARLIER_NAME in words and BAD not in words,
          "the report's historical navmesh caption says \"s's\" or omits the drawn mod", words)


def c2244_narration(_tmp):
    ch, _ = vignette(["--nav", "both", "--nav-version", f"{NR.SRC}={NR.EARLIER}"])
    said = " ".join(str((ch or {}).get("narration") or "").split())
    check("C2244", bool(ch) and EARLIER_NAME in said and BAD not in said,
          "the navmesh narration says \"s's\" or omits the earlier mod", said)


CASES = {"C2240": c2240_label_names_drawn_and_relation, "C2241": c2241_label_reads_the_frame,
         "C2242": c2242_label_without_version, "C2243": c2243_report_caption, "C2244": c2244_narration}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="", help="comma-separated case ids")
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    root = Path(tempfile.mkdtemp(prefix="nmc-"))
    for cid, fn in CASES.items():
        if only and cid not in only:
            continue
        try:
            fn(root)
        except Exception as e:  # noqa: BLE001  (a crash is reported as such, never as the case's own answer)
            FAILS.append(cid)
            RESULTS.append((cid, "CRASH", f"{type(e).__name__}: {e}"))
            traceback.print_exc()
    for cid, verdict, why in RESULTS:
        print(f"{verdict:6} {cid}" + (f"  {why}" if why else ""))
    print(f"{len(FAILS)} failing check(s)" if FAILS else "all cases GREEN")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
