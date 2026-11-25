"""Where the houseCARL integration module keeps its own data: the report and reel words and rules
(reporting-data/report-priorities.json), the scene policy (reporting-data/scene-policy.json), the narration words
(reporting-data/narration.json) and the agent definitions a models.* setting routes a step to (the module plugin's
claude-plugin/agents/).

The one owner of those places. They came from houseCARL's evaluation-reporting skill and plugin/agents; the skill is now
the module's own (claude-plugin/), and nothing reads houseCARL's copies (SkyWright.Guards fence). Paths are relative to this file, so a copy of the module (a suite's scratch
tree) reads its own copy.
"""
from __future__ import annotations

from pathlib import Path

MODULE = Path(__file__).resolve().parent
DATA = MODULE / "reporting-data"
PRIORITIES = DATA / "report-priorities.json"
SCENE_POLICY = DATA / "scene-policy.json"
#: SkyWright's grouping of review rows (groupKey, types, otherType), the app's own data, read from the checkout
GROUPS = MODULE.parent.parent / "data" / "scene-policy.json"
GROUP_KEYS = ("groupKey", "groupKeyWhy", "types", "otherType")
NARRATION = DATA / "narration.json"
PLUGIN = MODULE / "claude-plugin"   # the module as a Claude Code plugin (its manifest, skills and agents)
AGENTS = PLUGIN / "agents"


def scene_policy(presentation: Path | None = None, groups: Path | None = None) -> dict:
    """The scene policy the report and reel read: the module's reporting-data/scene-policy.json with SkyWright's grouping
    (data/scene-policy.json) joined into its `vignettes`. Each key has one owner: a key in both files is refused, as is a
    grouping file without its keys."""
    import json
    presentation = Path(presentation or SCENE_POLICY)
    groups = Path(groups or GROUPS)
    doc = json.loads(presentation.read_text(encoding="utf-8"))
    g = json.loads(groups.read_text(encoding="utf-8"))
    vign = doc.setdefault("vignettes", {})
    both = sorted(k for k in GROUP_KEYS if k in vign)
    if both:
        raise ValueError(f"{presentation} sets {', '.join(both)}, which is SkyWright's grouping ({groups}); remove it there, so each key has one owner.")
    missing = [k for k in GROUP_KEYS if k not in g]
    if missing:
        raise ValueError(f"SkyWright's grouping {groups} has no {', '.join(missing)}; the report and reel cannot group rows without it.")
    for k in GROUP_KEYS:
        vign[k] = g[k]
    return doc


def files() -> list[Path]:
    """Every data file the report and reel words and rules come from, for the stage tool hashes."""
    return [PRIORITIES, SCENE_POLICY, GROUPS, NARRATION]


def under(tree: Path) -> Path:
    """DATA as it sits in another copy of the checkout (a suite's scratch tree)."""
    return Path(tree) / DATA.relative_to(MODULE.parent.parent)
