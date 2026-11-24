"""Where the houseCARL integration module keeps its own data: the report and reel words and rules
(reporting-data/report-priorities.json), the scene policy (reporting-data/scene-policy.json), the narration words
(reporting-data/narration.json) and the agent definitions a models.* setting routes a step to (model-agents/).

The one owner of those places. They came from houseCARL's evaluation-reporting skill and plugin/agents, which the module
no longer reads (SkyWright.Guards fence). Paths are relative to this file, so a copy of the module (a suite's scratch
tree) reads its own copy.
"""
from __future__ import annotations

from pathlib import Path

MODULE = Path(__file__).resolve().parent
DATA = MODULE / "reporting-data"
PRIORITIES = DATA / "report-priorities.json"
SCENE_POLICY = DATA / "scene-policy.json"
NARRATION = DATA / "narration.json"
AGENTS = MODULE / "model-agents"


def under(tree: Path) -> Path:
    """DATA as it sits in another copy of the checkout (a suite's scratch tree)."""
    return Path(tree) / DATA.relative_to(MODULE.parent.parent)
