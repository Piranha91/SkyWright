"""Where SkyWright's built programs are, for the Python tools.

A build of the solution writes bin/<Configuration>/<.NET version>/ with SkyWright.exe, Probes/<name>/, Tools/<name>/,
Data/ and the marker skywright-layout.json. Directory.Build.props owns that layout and writes the marker; the C#
programs (CheckoutLayout.cs) and this module both read the marker instead of spelling a path.

The configuration is SKYWRIGHT_CONFIG, else Release: the optimized build, which evaluations and timings use. When no
build of that configuration exists, the paths returned point into a folder named for that fact, so a caller's
"not built at <path>" message says why.
"""
from __future__ import annotations

import json
import os
from pathlib import Path

CHECKOUT = Path(__file__).resolve().parent.parent
MARKER = "skywright-layout.json"


def configuration(config: str | None = None) -> str:
    return config or os.environ.get("SKYWRIGHT_CONFIG") or "Release"


_hits: dict[str, tuple[Path, dict]] = {}


def _found(config: str) -> tuple[Path, dict] | None:
    """The one build of a configuration and its marker, or None. Found builds are remembered; a miss is looked
    for again next time, so a build made later in the same process is seen. Never raises: a caller's own
    "not built at <path>" sentence says what is wrong, through the placeholder folder names below."""
    if config in _hits:
        return _hits[config]
    hits = sorted((CHECKOUT / "bin" / config).glob(f"*/{MARKER}"))
    if len(hits) != 1:
        return None
    try:
        spec = json.loads(hits[0].read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    _hits[config] = (hits[0].parent, spec)
    return _hits[config]


def why_no_build(config: str | None = None) -> str | None:
    """One sentence when a configuration has no usable build, else None."""
    cfg = configuration(config)
    if _found(cfg):
        return None
    hits = sorted((CHECKOUT / "bin" / cfg).glob(f"*/{MARKER}"))
    if len(hits) > 1:
        return (f"there is more than one SkyWright {cfg} build under {CHECKOUT / 'bin' / cfg} "
                f"({', '.join(h.parent.name for h in hits)}); delete the stale one.")
    if hits:
        return f"the marker {hits[0]} cannot be read; build the solution again (scripts/build.ps1)."
    return f"there is no SkyWright {cfg} build under {CHECKOUT / 'bin' / cfg}; build the solution (scripts/build.ps1)."


def root(config: str | None = None) -> Path:
    """The build folder (SkyWright.exe's folder) of a configuration; without one, a folder whose name says so."""
    cfg = configuration(config)
    found = _found(cfg)
    return found[0] if found else CHECKOUT / "bin" / cfg / f"no-usable-{cfg}-build-run-scripts-build.ps1"


def _spec(config: str | None) -> dict | None:
    found = _found(configuration(config))
    return found[1] if found else None


def program(name: str, config: str | None = None) -> Path:
    """A probe's (or conflictdiff's, navedit's) exe; runcontrol is a tool and comes back as such."""
    spec = _spec(config)
    if spec is None:
        return root(config) / f"{name}.exe"
    return _place(root(config), spec, name)


def _place(base: Path, spec: dict, name: str) -> Path:
    template = spec["tool"] if name in spec["tools"] else spec["program"]
    return base / template.replace("{name}", name)


def program_dir(name: str, config: str | None = None) -> Path:
    return program(name, config).parent


def marker_of(build_root: Path | str) -> dict | None:
    """The marker of a given build folder (any folder, not only this checkout's builds), or None without one."""
    p = Path(build_root) / MARKER
    return json.loads(p.read_text(encoding="utf-8")) if p.is_file() else None


def program_under(build_root: Path | str, name: str) -> Path:
    """program(name) for a given build folder, by that folder's own marker. A folder without a marker gives a path
    into a folder named for that fact, so a caller's "not built at <path>" message says why."""
    base = Path(build_root)
    spec = marker_of(base)
    if spec is None:
        return base / "(no SkyWright build here)" / f"{name}.exe"
    return _place(base, spec, name)


def program_dir_under(build_root: Path | str, name: str) -> Path:
    return program_under(build_root, name).parent


def piece_under(build_root: Path | str, key: str) -> Path:
    """piece(key) for a given build folder, by that folder's own marker (as program_under)."""
    base = Path(build_root)
    spec = marker_of(base)
    if spec is None:
        return base / "(no SkyWright build here)" / key
    return base / spec[key]


# The marker a test writes into a fake build folder when no real build exists to copy it from. Directory.Build.props
# owns the real one; this copy only stands in so tests can plant programs without a build.
_FALLBACK_MARKER = {
    "kind": "skywright-build", "configuration": "Release", "framework": "net9.0-windows10.0.19041.0", "app": "SkyWright.exe", "program": "Probes/{name}/{name}.exe",
    "tool": "Tools/{name}/{name}.exe", "tools": ["runcontrol"], "corpus": "Data/corpus.json",
    "registry": "Data/check-registry.json", "severity": "Data/workbench-severity.json",
    "scenePolicy": "Data/scene-policy.json",
}


def plant_marker(build_root: Path | str, config: str | None = None) -> dict:
    """Make a folder a fake build (for tests): write this checkout's marker into it (the fallback copy when there is
    no build) and return it. Programs then go where program_under(build_root, name) says."""
    spec = _spec(config) or dict(_FALLBACK_MARKER)
    base = Path(build_root)
    base.mkdir(parents=True, exist_ok=True)
    (base / MARKER).write_text(json.dumps(spec), encoding="utf-8")
    return spec


def piece(key: str, config: str | None = None) -> Path:
    """A named piece: "corpus", "registry", "severity", "scenePolicy", or "app". The build holds no houseCARL program
    (the app and runcontrol run houseCARL's code in process); the integration module's build holds the server."""
    spec = _spec(config)
    if spec is None:
        return root(config) / key
    return root(config) / spec[key]


def runcontrol(config: str | None = None) -> Path:
    return program("runcontrol", config)


def app(config: str | None = None) -> Path:
    return piece("app", config)
