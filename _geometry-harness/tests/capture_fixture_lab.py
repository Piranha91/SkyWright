"""The capture-fallback fixture laid out as an MO2 instance over a real Skyrim SE game's archives (tests/test_capture_fallbacks.py).

The plugin comes from `viewprobe --write-capture-fixture` (viewprobe/CaptureFixture.cs): interior cells whose STAT bases name
vanilla meshes. The meshes and textures are read from the game's own `Skyrim - *.bsa` archives, so the game folder is an
input: HOUSECARL_FIXTURE_GAME, else `fixtureGame` in the file HOUSECARL_HARNESS_CONFIG names. Neither set, or the folder
lacks the archives: `game_folder()` returns None with the reason, and the tests report NOT RUN (never a pass).
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

ARCHIVES = ["Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa", "Skyrim - Textures0.bsa", "Skyrim - Textures1.bsa",
            "Skyrim - Textures2.bsa", "Skyrim - Textures3.bsa", "Skyrim - Textures4.bsa", "Skyrim - Textures5.bsa",
            "Skyrim - Textures6.bsa", "Skyrim - Textures7.bsa", "Skyrim - Textures8.bsa", "Skyrim - Shaders.bsa"]
PLUGIN = "CfFixture.esp"
# the fixture's FormIDs (CaptureFixture.cs Ids(): cell base b, nav b+1, A b+2, B b+3; walls 0x2000 + cell index * 0x40 + n,
# n = side * 8 + tier * 2 + piece, sides W E S N, tiers bottom up; floor tiles 0x1000 + cell index * 0x40 + 0..48)
CELLS = {"CfCutaway": 0x810, "CfTouch": 0x820, "CfBuried": 0x830, "CfGlow": 0x840, "CfFocus": 0x850, "CfNoNav": 0x860,
         "CfDisabled": 0x870, "CfCutMod": 0x880}
PLUGIN_MOD = "CfFixtureMod.esp"      # CfCutMod's B (master CfFixture.esp); the "without" setup leaves it out
CUTMOD_B = f"000801:{PLUGIN_MOD}"


def fk(n: int) -> str:
    return f"{n:06X}:{PLUGIN}"


def ids(cell: str) -> dict:
    b = CELLS[cell]
    ci = (b - 0x810) // 0x10
    walls = {side: [fk(0x2000 + ci * 0x40 + s * 8 + n) for n in range(8)] for s, side in enumerate("WESN")}
    return {"cell": cell, "A": fk(b + 2), "B": fk(b + 3), "walls": walls,
            "allWalls": [k for side in "WESN" for k in walls[side]],
            # tiers 0 and 1 (z -42..110) reach the pair's height (0..40); 2 and 3 do not
            "lowWalls": [k for side in "WESN" for k in walls[side][:4]],
            "floor": [fk(0x1000 + ci * 0x40 + n) for n in range(49)]}


def game_folder() -> tuple[Path | None, str]:
    """The Skyrim SE game folder whose Data archives the fixture reads, or (None, why not)."""
    raw = os.environ.get("HOUSECARL_FIXTURE_GAME", "").strip()
    src = "HOUSECARL_FIXTURE_GAME"
    if not raw:
        cfg = os.environ.get("HOUSECARL_HARNESS_CONFIG", "").strip()
        if cfg and Path(cfg).is_file():
            try:
                raw = str(json.loads(Path(cfg).read_text(encoding="utf-8")).get("fixtureGame") or "").strip()
                src = f"fixtureGame in {cfg}"
            except (OSError, ValueError):
                raw = ""
    if not raw:
        return None, "no game data: set HOUSECARL_FIXTURE_GAME (or fixtureGame in the harness config) to a Skyrim SE game folder"
    game = Path(raw)
    missing = [a for a in ARCHIVES[:3] if not (game / "Data" / a).is_file()]
    if missing:
        return None, f"no game data: {src} names {game}, which has no Data\\{missing[0]}"
    return game, src


class Lab:
    """CfFixture.esp in an MO2 instance under `tmp`: one mod, one profile, a Skyrim.ini naming the base archives."""

    def __init__(self, tmp: Path, vp: Path, game: Path):
        self.tmp, self.vp, self.game = tmp, vp, game
        fx = tmp / "fixture"
        r = subprocess.run([str(vp), "--write-capture-fixture", str(fx)], capture_output=True, text=True, encoding="utf-8",
                           errors="replace")
        if r.returncode != 0 or not (fx / PLUGIN).is_file():
            raise RuntimeError(f"the fixture was not written (exit {r.returncode}): {r.stdout[-400:]} {r.stderr[-400:]}")
        self.inst = tmp / "instance"
        prof = self.inst / "profiles" / "P"
        prof.mkdir(parents=True, exist_ok=True)
        (self.inst / "overwrite").mkdir(exist_ok=True)
        mod = self.inst / "mods" / "CfFixture"
        mod.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(fx / PLUGIN, mod / PLUGIN)
        mod2 = self.inst / "mods" / "CfFixtureMod"
        mod2.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(fx / PLUGIN_MOD, mod2 / PLUGIN_MOD)
        (prof / "plugins.txt").write_text(f"*{PLUGIN}\n*{PLUGIN_MOD}\n", encoding="utf-8")
        (prof / "loadorder.txt").write_text(f"{PLUGIN}\n{PLUGIN_MOD}\n", encoding="utf-8")
        (prof / "modlist.txt").write_text("+CfFixtureMod\n+CfFixture\n", encoding="utf-8")
        (prof / "Skyrim.ini").write_text("[Archive]\nsResourceArchiveList=" + ", ".join(ARCHIVES[:2] + ARCHIVES[-1:])
                                         + "\nsResourceArchiveList2=" + ", ".join(ARCHIVES[2:-1]) + "\n", encoding="utf-8")
        self.cand = tmp / "candidate-cf.json"
        self.cand.write_text(json.dumps({"SchemaVersion": 1, "Name": "cf", "BaseProfile": "P", "Plugins": [PLUGIN],
                                         "Mods": ["CfFixture"], "Changes": []}), encoding="utf-8")
        # the setup with the second plugin (CfCutMod's B): the "with" arm of the B1 capture; `cand` is its "without"
        self.cand_mod = tmp / "candidate-cf-mod.json"
        self.cand_mod.write_text(json.dumps({"SchemaVersion": 1, "Name": "cf-mod", "BaseProfile": "P",
                                             "Plugins": [PLUGIN, PLUGIN_MOD], "Mods": ["CfFixtureMod", "CfFixture"],
                                             "Changes": ["added " + PLUGIN_MOD]}), encoding="utf-8")

    def target(self, order: Path | None = None, with_order: bool = True) -> list[str]:
        """viewprobe's target flags for this instance (what a capture request's passthrough carries); `with_order=False`
        leaves --order to the caller (the capture step names each state's setup itself)."""
        base = ["--instance", str(self.inst), "--profile", "P", "--game", str(self.game)]
        return base + (["--order", str(order or self.cand)] if with_order else [])

    def render(self, out: Path, cell: str, *flags: str) -> subprocess.CompletedProcess:
        out.mkdir(parents=True, exist_ok=True)
        cmd = [str(self.vp), cell, *flags, "--out", str(out), *self.target()]
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
        (out / "console.txt").write_text(" ".join(cmd) + "\n" + r.stdout + "\n--stderr--\n" + r.stderr, encoding="utf-8")
        return r


if __name__ == "__main__":
    # overview: one top-down and one eye-level frame per cell, for a person to check the layout
    import sys
    vp, out = Path(sys.argv[1]), Path(sys.argv[2])
    game, why = game_folder()
    if game is None:
        print("NOT RUN:", why)
        sys.exit(3)
    lab = Lab(out / "lab", vp, game)
    for cell in CELLS:
        i = ids(cell)
        r = lab.render(out / cell / "top", cell, "--eye", "15,-13,1400", "--look", "15,-12,0", "--size", "480", "--msaa", "0",
                       "--nav", "wire", "--ref", f"{i['A']}=cyan", "--ref", f"{i['B']}=magenta", "--ref-surface")
        print(cell, "top exit", r.returncode, r.stderr.strip()[-300:])
        r = lab.render(out / cell / "eye", cell, "--eye", "15,-313,120", "--look", "15,-13,40", "--size", "480", "--msaa", "0",
                       "--nav", "off", "--ref", f"{i['A']}=cyan", "--ref", f"{i['B']}=magenta", "--ref-surface")
        print(cell, "eye exit", r.returncode, r.stderr.strip()[-300:])
