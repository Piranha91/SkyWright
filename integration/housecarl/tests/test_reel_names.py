"""Known answers for declared names in the reel and the unpictured count line (C2166, C2167).

    python _geometry-harness/tests/test_reel_names.py [--only C2166]

C2166. Moon and Star's reel said its patch, DBM_MAS_Patch.esp, as "Patch" ("Patch is a patch for Legacy of the Dragonborn
and Moon and Star"; "which holds Patch, 0000DCD2.NIF"). The reel puts a mod's declared name in place of its plugin; the
final speech rule (C1961) then took the name for an editor ID, and humanize dropped the prefix DBM_ and the all-capital
word MAS. A declared mod name, and a plugin's file name without its extension, are said and shown as written. Wrong
answers: a declared name changed in the storyboard's speech, on a still page or in the render's speech; an undeclared
editor ID no longer said in words. The names are the round 2 subjects' declared patches (Moon and Star's, with the
all-capital word MAS; Khajiit Will Follow's and INIGO's).

C2167. The reel's unpictured line said "of 4 pairs, 2 pictured, 0 represented by the pictured example and 2 not
pictured". A count of 0 is left out with its words. Wrong answers: a "0 ..." clause; a non-zero count missing; the
clauses not joined with commas and a last "and".
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
HARNESS = HERE.parent
SKILL = SW.CHECKOUT / "external" / "houseCARL" / ".claude" / "skills" / "evaluation-reporting" / "references"
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(SW.SCRIPTS))

FAILED: list[str] = []

# the declared names of the round 2 subjects' patches, as their entries declare them
MOD_NAMES = {
    "MoonAndStar_MAS.esp": {"name": "Moon and Star", "source": "declared"},
    "LegacyoftheDragonborn.esm": {"name": "Legacy of the Dragonborn", "source": "declared"},
    "DBM_MAS_Patch.esp": {"name": "DBM_MAS_Patch", "source": "declared"},
    "KhajiitWillFollow.esp": {"name": "Khajiit Will Follow", "source": "declared"},
    "DBM_KhajiitFollow_Patch.esp": {"name": "DBM_KhajiitFollow_Patch", "source": "declared"},
    "Inigo.esp": {"name": "INIGO", "source": "declared"},
    "DBM_Inigo_Patch.esp": {"name": "DBM_Inigo_Patch", "source": "declared"},
}
ROWS = ["DBM_MAS_Patch is a patch for Legacy of the Dragonborn and Moon and Star.",
        "Nexus search for Moon and Star and Legacy of the Dragonborn: a file names both mods, Legacy of the Dragonborn "
        "Patches SE (Light Plugins), which holds DBM_MAS_Patch, 0000DCD2.NIF and 18 more.",
        "DBM_KhajiitFollow_Patch is a patch for Legacy of the Dragonborn and Khajiit Will Follow.",
        "DBM_Inigo_Patch is a patch for Legacy of the Dragonborn and INIGO.",
        "It places KhajiitWillFollow next to MoonAndStar_MAS.",
        "It removes the scripts from BUV_ControlQuestEffect_dunFrostmereCryptQST_90_100."]
KEPT = ["DBM_MAS_Patch is a patch", "which holds DBM_MAS_Patch,", "DBM_KhajiitFollow_Patch is a patch",
        "DBM_Inigo_Patch is a patch", "places KhajiitWillFollow next to MoonAndStar_MAS"]


def check(case, ok, wrong, detail=""):
    print(("  GREEN " if ok else "  RED   ") + f"{case}: " + ("" if ok else f"{wrong} ({detail})"))
    if not ok:
        FAILED.append(case)


def c2166_declared_names_said_as_written(tmp: Path):
    import reel_stills as RS
    reel_words = json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8")).get("reel") or {}
    fonts = {k: reel_words[k] for k in ("stillFont", "stillFontBold") if reel_words.get(k)}
    pres = {"presentationId": "c2166", "chapters": [], "findings": [], "evidence": [], "settings": {"reel": fonts},
            "reel": {"status": "planned", "modNames": MOD_NAMES, "chapters": [
                {"id": "R-01", "kind": "verdict", "narration": " ".join(ROWS),
                 "still": {"pages": [{"layout": "list", "title": "What to do", "rows": list(ROWS)}]}},
                {"id": "R-02", "kind": "vignette", "narration": ROWS[0] + " " + ROWS[3], "frames": []}]}}
    root = tmp / "c2166"
    (root / "bundle").mkdir(parents=True, exist_ok=True)
    (root / "presentation.json").write_text(json.dumps(pres), encoding="utf-8")
    r = subprocess.run([sys.executable, str(HARNESS / "evalreel.py"), "storyboard-packet", "--presentation",
                        str(root / "presentation.json"), "--bundle", str(root / "bundle"), "--out", str(root / "sb.json")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    sb = json.loads((root / "sb.json").read_text(encoding="utf-8")) if (root / "sb.json").is_file() else {}
    said = " ".join(s_.get("narration") or "" for s_ in sb.get("scenes") or [])
    lost = [k for k in KEPT if k not in said]
    check("C2166", r.returncode == 0 and said and not lost and not re.search(r"(?<![_A-Za-z])Patch is a patch|holds Patch,", said),
          "the storyboard's speech changed a declared name", f"exit {r.returncode} {r.stderr[-200:]!r}; lost {lost}; {said[:300]!r}")
    # the render's speech and the still pages: the same names, set from the reel plan's modNames by the render itself
    src = (HARNESS / "evalreel.py").read_text(encoding="utf-8")
    check("C2166", src.count('reel_stills.keep_names(reel.get("modNames") or {}, reel.get("shortForms"))') == 2,
          "the render or the storyboard does not hand the reel's declared names to the speech rule")
    getattr(RS, "keep_names", lambda _names: None)(MOD_NAMES)   # absent before C2166: then nothing is kept
    shown = " ".join(RS.shown_text(x) for x in ROWS)
    lost = [k for k in KEPT if k not in shown]
    check("C2166", not lost, "a still page shows a declared name changed", f"lost {lost}; {shown[:300]!r}")
    # an editor ID nobody declared is still said in words, and the shapes check does not count a declared name
    worded = RS.spoken_text(ROWS[5])
    check("C2166", "BUV_" not in worded and "Control Quest Effect" in worded
          and RS.speech_shapes(RS.spoken_text(" ".join(ROWS))) == [],
          "an undeclared editor ID is no longer said in words, or a declared name counts as one", worded)


def c2167_zero_counts_left_out(tmp: Path):
    import reel_plan as RP
    words = (json.loads((SKILL / "report-priorities.json").read_text(encoding="utf-8")).get("reel") or {}).get("words") or {}
    def before_c2167(w, p, r, u):
        # the line as the reel filled it before C2167: every count from the one template, 0 or not
        return (w.get("unpicturedAccount", "").format(label="", total=0, unit="", pictured=p, represented=r,
                                                       unpictured=u, counts="").split(", ", 1)[-1])
    counts = getattr(RP, "account_counts", None) or before_c2167
    got = {k: counts(words, *k) for k in ((2, 0, 2), (2, 1, 0), (0, 0, 3), (2, 1, 2), (1, 0, 0))}
    want = {(2, 0, 2): "2 pictured and 2 not pictured",
            (2, 1, 0): "2 pictured and 1 represented by the pictured example",
            (0, 0, 3): "3 not pictured",
            (2, 1, 2): "2 pictured, 1 represented by the pictured example and 2 not pictured",
            (1, 0, 0): "1 pictured"}
    line = words.get("unpicturedAccount", "").format(label="Objects occupying the same space", total=4, unit="pairs",
                                                     counts=got[(2, 0, 2)], pictured=2, represented=0, unpictured=2)
    check("C2167", got == want and line == "Objects occupying the same space: of 4 pairs, 2 pictured and 2 not pictured"
          and not re.search(r"\b0 ", line),
          "a count of 0 is said, a count is missing, or the clauses are joined wrongly",
          f"{ {str(k): v for k, v in got.items()} }; {line!r}")


CASES = {"C2166": c2166_declared_names_said_as_written, "C2167": c2167_zero_counts_left_out}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    a = ap.parse_args(argv)
    only = {s.strip() for s in a.only.split(",") if s.strip()}
    with tempfile.TemporaryDirectory(prefix="reel-names-") as td:
        for cid, fn in CASES.items():
            if only and cid not in only:
                continue
            try:
                fn(Path(td))
            except Exception as e:  # a case that cannot run is red, never skipped
                print(f"  CRASH {cid}: {type(e).__name__}: {e}")
                FAILED.append(cid)
    print("ALL GREEN" if not FAILED else f"RED: {sorted(set(FAILED))}")
    return 0 if not FAILED else 1


if __name__ == "__main__":
    sys.exit(main())
