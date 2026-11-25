"""buildlayout.py against the build the solution wrote: its fallback marker (used only to plant fake builds in tests
when no build exists) must equal the marker Directory.Build.props writes, except the configuration; and the paths
it gives for a probe, runcontrol and the server must exist in that build. Exit 0 when every check passes; exit 0
with "skipped" when there is no Release build to compare with."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import buildlayout  # noqa: E402

failures = []


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else f": {detail}"))
    if not ok:
        failures.append(name)


why = buildlayout.why_no_build("Release")
if why:
    print("skipped: " + why)
    sys.exit(0)

real = json.loads((buildlayout.root("Release") / buildlayout.MARKER).read_text(encoding="utf-8"))
fallback = dict(buildlayout._FALLBACK_MARKER)
for key in sorted(set(real) | set(fallback)):
    if key == "configuration":
        continue
    check(f"fallback marker key {key}", real.get(key) == fallback.get(key), f"build {real.get(key)!r}, fallback {fallback.get(key)!r}")
check("the build's configuration", real.get("configuration") == "Release", real.get("configuration"))
for label, path in [("overlapprobe", buildlayout.program("overlapprobe", "Release")),
                    ("runcontrol", buildlayout.runcontrol("Release")),
                    ("app", buildlayout.app("Release"))]:
    check(f"{label} exists at {path}", path.is_file())
check("the build's marker names no houseCARL server", "server" not in real and "housecarl-mcp" not in real.get("tools", []), real)
check("buildlayout has no server piece", not hasattr(buildlayout, "server"))
check("a fake build folder gets the placeholder", "no SkyWright build here" in str(buildlayout.program_under(Path("Z:/nowhere"), "x")))

print(f"{len(failures)} failed")
sys.exit(1 if failures else 0)
