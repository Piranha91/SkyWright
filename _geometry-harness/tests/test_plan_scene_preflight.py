"""`evalrun plan` runs overlapprobe's scene preflight on every arm (cases 440-451).

    python _geometry-harness/tests/test_plan_scene_preflight.py

A plan used to accept arms whose asset view the base profile cannot produce; overlapprobe's scene preflight then
refused them hours later, at the overlap scenes. The plan now runs that same preflight (the probe's own code, not a
copy) and refuses such an arm with the probe's reason and what to try. Every case builds a temporary MO2 instance
(never a real install) and drives the REAL evalrun.py and the REAL overlapprobe built in this checkout
(`dotnet build _geometry-harness/overlapprobe/overlapprobe.csproj -c Release`). A missing exe is a FAILURE, never a
skip. No load order is resolved beyond reading modlist.txt, except case 450's ordinary --order read.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
from test_evalrun import CONTROLLER  # noqa: E402  (the built-in controller, runcontrol, as test_evalrun starts it)
REGISTRY = REPO / "data" / "check-registry.json"
EXE = HARNESS / "overlapprobe" / "bin" / "Release" / "net9.0" / ("overlapprobe.exe" if os.name == "nt" else "overlapprobe")

FAILURES: list[str] = []


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def clean_env() -> dict:
    """No instance may leak in from the machine: the explicit-instance rule reads these."""
    env = {k: v for k, v in os.environ.items()
           if k not in ("HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH")}
    env["PYTHONIOENCODING"] = "utf-8"
    return env


def make_instance(root: Path) -> tuple[Path, Path]:
    """Profile P lists High above Low (strongest first) and Mid disabled between them; Other has no line."""
    inst, game = root / "instance", root / "game"
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True)
    for m in ("High", "Mid", "Low", "Other"):
        (inst / "mods" / m).mkdir(parents=True)
    (inst / "overwrite").mkdir()
    (game / "Data").mkdir(parents=True)
    (prof / "modlist.txt").write_text("+High\n-Mid\n+Low\n", encoding="utf-8")
    (prof / "loadorder.txt").write_text("Skyrim.esm\n", encoding="utf-8")
    (prof / "plugins.txt").write_text("*Skyrim.esm\n", encoding="utf-8")
    return inst, game


def candidate(folder: Path, stem: str, mods, base_profile="P", provenance=None) -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    doc = {"SchemaVersion": 1, "Name": stem, "BaseProfile": base_profile, "Plugins": ["Skyrim.esm"],
           "Changes": ["fixture"], "Provenance": provenance or {"why": "fixture"}}
    if mods is not None:
        doc["Mods"] = mods
    p = folder / f"{stem}.json"
    p.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    return p


def plan(root: Path, cands: Path, *, instance=None, game=None, profile="P", harness=HARNESS, extra=()):
    out = root / "run"
    if out.exists():
        shutil.rmtree(out)
    args = [*CONTROLLER, "plan", "--registry", str(REGISTRY), "--candidates", str(cands),
            "--out", str(out), "--harness", str(harness), "--depth", "standard"]
    if instance is not None:
        args += ["--instance", str(instance), "--game", str(game), "--profile", profile]
    args += list(extra)
    r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace",
                       env=clean_env(), timeout=300)
    doc = json.loads((out / "plan.json").read_text(encoding="utf-8")) if (out / "plan.json").exists() else None
    return r, doc


def arm(doc, arm_id):
    return next((a for a in (doc or {}).get("arms", []) if a.get("armId") == arm_id), {})


def preflight(cand: Path, inst: Path, game: Path, out: Path, profile="P"):
    r = subprocess.run([str(EXE), "--instance", str(inst), "--game", str(game), "--profile", profile,
                        "--order", str(cand), "--scene-preflight", "--json", str(out)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", env=clean_env(), timeout=120)
    doc = json.loads(out.read_text(encoding="utf-8")) if out.exists() else None
    return r, doc


def case_440(root: Path):
    """A Mods folder the base profile's modlist.txt does not list is refused at plan time."""
    inst, game = make_instance(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["High", "Low"])
    candidate(c, "candidate-01-typo", ["High", "Other"])
    r, doc = plan(root, c, instance=inst, game=game)
    check(r.returncode == 2, f"440: plan refuses (exit {r.returncode})")
    check(doc is None, "440: no plan.json is written")
    check("candidate-01-typo" in r.stderr and "has no provider by that name" in r.stderr,
          "440: the refusal names the arm and carries the probe's reason")
    check("modlist.txt lists every folder" in r.stderr, "440: the refusal says what to try")


def case_441(root: Path):
    """P50: a candidate that states its own asset priority (reversed against the profile) plans; the arm says so."""
    inst, game = make_instance(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["High", "Low"])
    candidate(c, "candidate-01-reversed", ["Low", "High"])
    r, doc = plan(root, c, instance=inst, game=game)
    check(r.returncode == 0 and doc is not None,
          f"441: plan accepts a candidate's own priority (exit {r.returncode}) {r.stderr.strip()[:300]}")
    pf = arm(doc, "candidate-01-reversed").get("scenePreflight") or {}
    check(pf.get("status") == "passed" and pf.get("reorderedProviders") == ["Low", "High"],
          f"441: the reversed arm passes and names the providers it re-orders ({pf})")
    base = arm(doc, "baseline").get("scenePreflight") or {}
    check(base.get("status") == "passed" and base.get("reorderedProviders") == [],
          f"441: the arm in the profile's order re-orders nothing ({base})")
    check("candidate-01-reversed" in r.stdout and "Low" in r.stdout and "its own" in r.stdout,
          "441: the plan output says which arm states its own asset priority")


def case_442(root: Path):
    """A candidate naming another profile than --profile is refused."""
    inst, game = make_instance(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["High", "Low"], base_profile="Manual")
    r, doc = plan(root, c, instance=inst, game=game)
    check(r.returncode == 2 and doc is None, f"442: plan refuses a BaseProfile mismatch (exit {r.returncode})")
    check("candidate BaseProfile 'Manual' does not match --profile 'P'" in r.stderr,
          "442: the probe's BaseProfile sentence reaches the caller")


def case_443(root: Path):
    """Every bad arm is listed in one attempt, not only the first."""
    inst, game = make_instance(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["Other"])
    candidate(c, "candidate-01-ok", ["High"])
    candidate(c, "candidate-02-twice", ["Low", "Low"])
    r, doc = plan(root, c, instance=inst, game=game)
    lines = [ln for ln in r.stderr.splitlines() if "scene preflight refused" in ln]
    check(r.returncode == 2 and doc is None, f"443: plan refuses (exit {r.returncode})")
    check(len(lines) == 2 and any("baseline" in ln for ln in lines) and any("candidate-02-twice" in ln for ln in lines),
          f"443: both bad arms are listed, the good one is not ({len(lines)} refusal line(s))")


def case_444(root: Path):
    """A candidate without Mods is legitimate for the ordersweep probes: recorded, not refused."""
    inst, game = make_instance(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["High", "Low"])
    candidate(c, "candidate-01-noview", None)
    r, doc = plan(root, c, instance=inst, game=game)
    sp = arm(doc, "candidate-01-noview").get("scenePreflight") or {}
    check(r.returncode == 0 and doc is not None, f"444: plan is written (exit {r.returncode})")
    check(sp.get("status") == "no-asset-view" and sp.get("code") == "no-explicit-mods",
          f"444: the arm records no-asset-view ({sp.get('status')}, {sp.get('code')})")
    check("candidate-01-noview" in r.stdout and "scene" in r.stdout, "444: the plan output says scenes cannot be captured from it")


def case_445(root: Path):
    """A passing arm records the verdict and the tool that gave it."""
    inst, game = make_instance(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["High", "Mid", "Low"])
    r, doc = plan(root, c, instance=inst, game=game)
    sp = arm(doc, "baseline").get("scenePreflight") or {}
    check(r.returncode == 0, f"445: plan is written (exit {r.returncode}) {r.stderr[-300:]}")
    check(sp.get("status") == "passed" and sp.get("providers") == 3, f"445: passed with 3 providers ({sp})")
    tool = sp.get("tool") or {}
    check(len(str(tool.get("dllSha256") or "")) == 64, "445: the verdict names the overlapprobe build that gave it")


def case_446(root: Path):
    """No instance named: not run, said so, plan still written."""
    root.mkdir(parents=True, exist_ok=True)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["Other"])
    r, doc = plan(root, c)
    sp = arm(doc, "baseline").get("scenePreflight") or {}
    check(r.returncode == 0 and doc is not None, f"446: plan is written without an instance (exit {r.returncode})")
    check(sp.get("status") == "not-run", f"446: the arm records not-run ({sp.get('status')})")
    check("asset views were not checked" in r.stdout, "446: the plan prints that the asset views were not checked")


def case_447(root: Path):
    """An instance is named but overlapprobe is not built: refused, never a silent skip."""
    inst, game = make_instance(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["High"])
    empty = root / "harness-without-probes"
    empty.mkdir()
    r, doc = plan(root, c, instance=inst, game=game, harness=empty)
    check(r.returncode == 2 and doc is None, f"447: plan refuses without overlapprobe (exit {r.returncode})")
    check("build overlapprobe" in r.stderr, "447: the refusal says to build overlapprobe")


def case_448(root: Path):
    """--probe-cmd overlapprobe=... is the program the plan runs, with the run's flags."""
    inst, game = make_instance(root)
    c = root / "c"
    candidate(c, "candidate-00-x-baseline", ["Other"])
    log = root / "argv.json"
    fake = root / "fake_overlapprobe.py"
    fake.write_text(
        "import json, sys\n"
        "a = sys.argv[1:]\n"
        f"open(r'{log}', 'w').write(json.dumps(a))\n"
        "out = a[a.index('--json') + 1]\n"
        "open(out, 'w').write(json.dumps({'schemaVersion': 1, 'verdict': 'passed', 'code': None, 'reason': None,"
        " 'remedy': None, 'providers': []}))\n", encoding="utf-8")
    r, doc = plan(root, c, instance=inst, game=game, extra=["--probe-cmd", f"overlapprobe={sys.executable} {fake}"])
    argv = json.loads(log.read_text(encoding="utf-8")) if log.exists() else []
    check(r.returncode == 0, f"448: the stand-in's pass is the plan's verdict (exit {r.returncode})")
    check("--scene-preflight" in argv and "--order" in argv and argv[argv.index("--profile") + 1] == "P"
          if "--profile" in argv else False, f"448: the stand-in received the preflight argv ({argv})")


def case_449(root: Path):
    """overlapprobe --scene-preflight --json writes a verdict with a code and a remedy for each refusal kind."""
    inst, game = make_instance(root)
    c = root / "c"
    rows = [("ok", ["High", "Low"], "P", None, "passed", None),
            ("typo", ["Other"], "P", None, "refused", "provider-not-in-modlist"),
            ("reversed", ["Low", "High"], "P", None, "passed", None),
            ("twice", ["High", "High"], "P", None, "refused", "provider-listed-twice"),
            ("blank", [" "], "P", None, "refused", "provider-empty-name"),
            ("noview", None, "P", None, "refused", "no-explicit-mods"),
            ("otherprofile", ["High"], "Manual", None, "refused", "base-profile-mismatch")]
    for stem, mods, bp, prov, verdict, code in rows:
        p = candidate(c, stem, mods, base_profile=bp, provenance=prov)
        r, doc = preflight(p, inst, game, root / f"{stem}.preflight.json")
        doc = doc or {}
        check(doc.get("verdict") == verdict and doc.get("code") == code and r.returncode == (0 if verdict == "passed" else 2),
              f"449: {stem}: verdict {doc.get('verdict')} code {doc.get('code')} exit {r.returncode}")
        if verdict == "refused":
            check(bool(doc.get("reason")) and bool(doc.get("remedy")), f"449: {stem}: reason and remedy are both stated")
    bad = c / "unreadable.json"
    bad.write_text("{ not json", encoding="utf-8")
    r, doc = preflight(bad, inst, game, root / "unreadable.preflight.json")
    check((doc or {}).get("code") == "candidate-unreadable", f"449: unreadable: code {(doc or {}).get('code')}")
    shutil.rmtree(inst / "mods" / "Low")
    p = candidate(c, "gone", ["High", "Low"])
    r, doc = preflight(p, inst, game, root / "gone.preflight.json")
    check((doc or {}).get("code") == "provider-folder-missing", f"449: gone: code {(doc or {}).get('code')}")


def case_450(root: Path):
    """SW13: a list-valued Provenance (evalreport reads provenance.patches as a list) is read by the probes."""
    inst, game = make_instance(root)
    c = root / "c"
    p = candidate(c, "candidate-00-x-baseline", ["High"], provenance={"patches": ["A.esp", "B.esp"], "why": "x"})
    r, doc = preflight(p, inst, game, root / "sw13.preflight.json")
    check(r.returncode == 0 and (doc or {}).get("verdict") == "passed",
          f"450: scene preflight passes a list-valued provenance (exit {r.returncode}) {r.stderr.strip()[:200]}")
    r2 = subprocess.run([str(EXE), "--instance", str(inst), "--game", str(game), "--profile", "P", "--order", str(p),
                         "--json", str(root / "sw13.rows.json")],
                        capture_output=True, text=True, encoding="utf-8", errors="replace", env=clean_env(), timeout=120)
    text = r2.stdout + r2.stderr
    check("could not be converted" not in text and "provides a file by that name" in text,
          "450: the ordinary --order read gets past the candidate file to the next check (no Skyrim.esm here)")


def case_451(root: Path):
    """P50: the receipt carries each provider's candidate rank and base-profile priority, and the re-ordered names."""
    inst, game = make_instance(root)
    c = root / "c"
    p = candidate(c, "mixed", ["Low", "Mid", "High"])
    r, doc = preflight(p, inst, game, root / "mixed.preflight.json")
    doc = doc or {}
    rows = [(v.get("mo2Name"), v.get("candidateRank"), v.get("mo2Priority")) for v in doc.get("providers") or []]
    check(r.returncode == 0 and doc.get("verdict") == "passed", f"451: passed (exit {r.returncode}) {r.stderr.strip()[:200]}")
    check(rows == [("Low", 0, 2), ("Mid", 1, 1), ("High", 2, 0)], f"451: candidate rank and MO2 priority per provider ({rows})")
    check(doc.get("reorderedProviders") == ["Low", "High"], f"451: re-ordered providers ({doc.get('reorderedProviders')})")
    check("its own order" in r.stdout and "Low" in r.stdout, "451: stdout says the probes read the candidate's order")
    same = candidate(c, "same", ["High", "Low"])
    _, sdoc = preflight(same, inst, game, root / "same.preflight.json")
    check((sdoc or {}).get("reorderedProviders") == [], f"451: profile order re-orders nothing ({(sdoc or {}).get('reorderedProviders')})")


CASES = {"case-440": case_440, "case-441": case_441, "case-442": case_442, "case-443": case_443,
         "case-444": case_444, "case-445": case_445, "case-446": case_446, "case-447": case_447,
         "case-448": case_448, "case-449": case_449, "case-450": case_450,
         "case-451": case_451}


def main() -> int:
    want = sys.argv[1] if len(sys.argv) > 1 else ""
    if not EXE.exists():
        print(f"  FAIL  overlapprobe is not built at {EXE}; build it (dotnet build -c Release). Not a skip.")
        return 1
    tmp = Path(tempfile.mkdtemp(prefix="hc-plan-preflight-"))
    ran = 0
    try:
        for name, fn in CASES.items():
            if want and not name.startswith(want):
                continue
            ran += 1
            print(f"\n=== {name} ===")
            try:
                fn(tmp / name)
            except Exception as ex:
                check(False, f"{name}: raised {type(ex).__name__}: {ex}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if not ran:
        print(f"  FAIL  no case matches {want!r}")
        return 2
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
