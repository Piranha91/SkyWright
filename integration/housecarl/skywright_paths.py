"""Where SkyWright's checkout is, seen from the houseCARL integration module (integration/housecarl/).

The module uses SkyWright; SkyWright never reads the module. This is the one place the module says where the checkout
and its tool folders are: importing it puts SkyWright's Python tools (_geometry-harness/) and its run log (scripts/)
on sys.path after the module's own folder, so `import buildlayout`, `import toolidentity` and `from runlog import Run`
work from every script and suite here. SkyWright's built programs are then found as everywhere else, through
buildlayout.

The module's own built programs (lookprobe, winrt-tts, houseCARL's server) are in its build, bin/<Configuration>/, named
by the marker housecarl-integration-layout.json that Directory.Build.props writes; tool() and program() read it, so no
script here spells a path into that build. The configuration is the one buildlayout picks (SKYWRIGHT_CONFIG, else
Release).
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

MODULE = Path(__file__).resolve().parent
CHECKOUT = MODULE.parent.parent
HARNESS = CHECKOUT / "_geometry-harness"
SCRIPTS = CHECKOUT / "scripts"
DATA = CHECKOUT / "data"
HOUSECARL = CHECKOUT / "external" / "houseCARL"

for _p in (MODULE, SCRIPTS, HARNESS):
    if str(_p) not in sys.path:
        sys.path.insert(0, str(_p))

LAYOUT_MARKER = "housecarl-integration-layout.json"


def build_root(config: str | None = None) -> Path:
    """The module's build folder for a configuration (bin/<Configuration>/)."""
    import buildlayout
    return MODULE / "bin" / buildlayout.configuration(config)


def why_no_build(config: str | None = None) -> str | None:
    """One sentence when the module has no usable build of a configuration, else None."""
    marker = build_root(config) / LAYOUT_MARKER
    if not marker.is_file():
        return f"the houseCARL integration module is not built at {marker.parent}; build it (scripts/build.ps1)."
    try:
        json.loads(marker.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return f"the marker {marker} cannot be read; build the module again (scripts/build.ps1)."
    return None


def _place(name: str, config: str | None) -> Path:
    base = build_root(config)
    if why_no_build(config):
        return base / "(no houseCARL integration build here)" / f"{name}.exe"
    spec = json.loads((base / LAYOUT_MARKER).read_text(encoding="utf-8"))
    template = spec["tool"] if name in spec["tools"] else spec["program"]
    return base / template.replace("{name}", name)


def tool(name: str, config: str | None = None) -> Path:
    """A tool of the module's build (winrt-tts, housecarl-mcp); without a build, a path whose folder says so."""
    return _place(name, config)


def program(name: str, config: str | None = None) -> Path:
    """A probe of the module's build (lookprobe); without a build, a path whose folder says so."""
    return _place(name, config)


def server(config: str | None = None) -> Path:
    """houseCARL's server program in the module's build (Tools/housecarl-mcp/, its rulebook corpus.json beside it)."""
    return tool("housecarl-mcp", config)
