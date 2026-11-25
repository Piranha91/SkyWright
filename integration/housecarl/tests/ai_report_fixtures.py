"""The AI-side half of the report fixtures: what an AI-side evaluation adds to an app bundle the app-side maker
(_geometry-harness/tests/fixtures/evalreport/make_report_fixtures.py) made. Passed to its generate() as ai_hook.

R5's supplied media goes to an overlay (evalcontrol, this module's controller of the AI-side rows): planned over R5's
app run, evaluated, finalized, bundled with --media beside the app bundle, and verified with it. The overlay run is
RUN/ai, its bundle RUN/ai-bundle (bound to RUN/bundle as ../bundle), recorded in the case as aiBundle.
"""
from __future__ import annotations

import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
import sys
sys.path.insert(0, str(HERE.parent))
import skywright_paths as SW  # noqa: E402


def evalcontrol(*args) -> subprocess.CompletedProcess:
    return subprocess.run([str(SW.tool("evalcontrol")), *map(str, args)], capture_output=True, text=True, encoding="utf-8",
                          timeout=600)


def add_overlays(root: Path, index: dict) -> None:
    for case, entry in index.items():
        if not isinstance(entry, dict) or not entry.get("media"):
            continue
        run = Path(entry["run"])
        ai, ai_bundle = run / "ai", run / "ai-bundle"
        steps = [("plan", ["plan", "--app", run, "--app-bundle", entry["bundle"], "--out", ai]),
                 ("evaluate", ["evaluate", "--run", ai]),
                 ("finalize", ["finalize", "--run", ai]),
                 ("bundle", ["bundle", "--run", ai, "--out", ai_bundle, "--media", entry["media"]]),
                 ("verify", ["verify", ai_bundle])]
        for name, argv in steps:
            r = evalcontrol(*argv)
            if r.returncode != 0:
                raise SystemExit(f"{case}: evalcontrol {name} exited {r.returncode}\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}")
        (ai_bundle / "verify.json").unlink(missing_ok=True)
        (ai_bundle / "verify-app.json").unlink(missing_ok=True)
        entry["aiBundle"] = str(ai_bundle)
        print(f"  {case}: AI-side overlay bundle at {ai_bundle}")
