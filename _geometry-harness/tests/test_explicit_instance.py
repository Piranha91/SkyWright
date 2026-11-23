"""Known answers for the explicit-instance rule in `_shared/HarnessTarget.cs`.

    python _geometry-harness/tests/test_explicit_instance.py

A probe that resolves a load order never takes the MO2 instance from the harness config file. The instance comes
from the probe's own --instance flag or from HOUSECARL_MO2_INSTANCE set for that run; with neither, the probe refuses
with the named reason `instance-not-explicit` before it reads anything. The config's `game` and `profile` describe
the config's own instance, so they are used only when the explicit instance IS that instance; for any other instance
they are withheld and the probe refuses with `game-not-for-this-instance` / `profile-not-for-this-instance` unless the
run names them itself.

Drives two REAL built probes against a temporary config whose instance is an empty temporary folder (never a real
install): switchprobe, which has --instance/--game/--profile flags, and geoprobe, which has none and takes the
environment; and corridorprobe, which requires its game before its instance. Each cell names the answer the unchanged tree got wrong or must keep. No load order is read.

The Release builds must exist: `dotnet build _geometry-harness/<probe>/<probe>.csproj -c Release`. A missing exe is
a FAILURE, never a skip.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import json
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
sys.path.append(str(HARNESS))
import buildlayout  # noqa: E402  where the build put each program
FAILURES, PASSED = [], []

REFUSALS = ("instance-not-explicit", "instance-env-persistent", "game-not-for-this-instance",
            "profile-not-for-this-instance")


def check(ok: bool, label: str, detail: str = "") -> None:
    (PASSED if ok else FAILURES).append(label)
    print(("PASS " if ok else "FAIL ") + label + ("" if ok or not detail else "\n      " + detail.replace("\n", "\n      ")))


def exe(name: str) -> Path:
    return buildlayout.program(name)


def run(probe: str, args: list[str], tmp: Path, env_extra: dict[str, str] | None = None) -> tuple[int, str]:
    env = {k: v for k, v in os.environ.items()
           if k not in ("HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH",
                        "HOUSECARL_HARNESS_TEST_PERSISTED_INSTANCE")}
    env["HOUSECARL_HARNESS_CONFIG"] = str(tmp / "harness.json")
    env["HOUSECARL_RUNLOG_DIR"] = str(tmp / "runlog")
    env.update(env_extra or {})
    try:
        p = subprocess.run([str(exe(probe))] + args, env=env, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=90)
        return p.returncode, (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired as ex:
        return -1, f"timeout: {ex}"


def refusals_in(text: str) -> list[str]:
    return [r for r in REFUSALS if r in text]


def main() -> int:
    for probe in ("switchprobe", "geoprobe", "corridorprobe"):
        if not exe(probe).exists():
            check(False, f"{probe} Release build exists", str(exe(probe)))
    if FAILURES:
        return 1

    tmp = Path(tempfile.mkdtemp(prefix="hc-explicit-instance-"))
    try:
        cfg_inst, cfg_game = tmp / "cfgInstance", tmp / "cfgGame"
        other_inst, other_game = tmp / "otherInstance", tmp / "otherGame"
        for d in (cfg_inst / "profiles" / "CfgProf", cfg_game / "Data",
                  other_inst / "profiles" / "OtherProf", other_game / "Data"):
            d.mkdir(parents=True)
        (tmp / "harness.json").write_text(json.dumps({
            "instance": cfg_inst.as_posix(), "profile": "CfgProf", "game": cfg_game.as_posix()}), encoding="utf-8")

        # K1 — no instance anywhere: refuse by name, exit 2, and say the config's instance was not used.
        # The unchanged tree took the config's instance and went on to read it.
        for probe in ("switchprobe", "geoprobe"):
            code, out = run(probe, [], tmp)
            check(code == 2 and refusals_in(out) == ["instance-not-explicit"] and cfg_inst.as_posix() in out,
                  f"K1 {probe}: no instance given -> refused instance-not-explicit (exit 2, names the unused config instance)",
                  f"exit {code}\n{out[-1500:]}")

        # K2 — the explicit instance IS the config's instance (spelled differently: backslashes, trailing slash,
        # other case): the config's game and profile are its own and are used. Kept from the unchanged tree.
        spelled = str(cfg_inst).replace("/", "\\").upper() + "\\"
        code, out = run("switchprobe", ["--instance", spelled], tmp)
        check(not refusals_in(out) and "load order:" in out, "K2 switchprobe: --instance = config instance (other spelling) -> config game/profile used, no refusal",
              f"exit {code}\n{out[-1500:]}")
        code, out = run("geoprobe", [], tmp, {"HOUSECARL_MO2_INSTANCE": cfg_inst.as_posix()})
        check(not refusals_in(out), "K2 geoprobe: HOUSECARL_MO2_INSTANCE = config instance -> config game/profile used, no refusal",
              f"exit {code}\n{out[-1500:]}")

        # K3 — another instance with no game: the config's game belongs to the config's instance and is withheld.
        # The unchanged tree paired the other instance with the config's game folder.
        code, out = run("switchprobe", ["--instance", other_inst.as_posix()], tmp)
        check(code == 2 and refusals_in(out) == ["game-not-for-this-instance"],
              "K3 switchprobe: other --instance, no --game -> refused game-not-for-this-instance",
              f"exit {code}\n{out[-1500:]}")
        code, out = run("geoprobe", [], tmp, {"HOUSECARL_MO2_INSTANCE": other_inst.as_posix()})
        check(code == 2 and refusals_in(out) == ["game-not-for-this-instance"],
              "K3 geoprobe: other HOUSECARL_MO2_INSTANCE, no game -> refused game-not-for-this-instance",
              f"exit {code}\n{out[-1500:]}")

        # K4 — another instance with a game but no profile: the config's profile is withheld the same way.
        code, out = run("switchprobe", ["--instance", other_inst.as_posix(), "--game", other_game.as_posix()], tmp)
        check(code == 2 and refusals_in(out) == ["profile-not-for-this-instance"],
              "K4 switchprobe: other --instance and --game, no --profile -> refused profile-not-for-this-instance",
              f"exit {code}\n{out[-1500:]}")

        # K5 — everything explicit: no refusal; the probe goes on exactly as before.
        code, out = run("switchprobe", ["--instance", other_inst.as_posix(), "--game", other_game.as_posix(),
                                        "--profile", "OtherProf"], tmp)
        check(not refusals_in(out) and "load order:" in out, "K5 switchprobe: --instance, --game, --profile all given -> no refusal",
              f"exit {code}\n{out[-1500:]}")
        code, out = run("geoprobe", [], tmp, {"HOUSECARL_MO2_INSTANCE": other_inst.as_posix(),
                                               "HOUSECARL_GAME_PATH": other_game.as_posix(),
                                               "HOUSECARL_MO2_PROFILE": "OtherProf"})
        check(not refusals_in(out), "K5 geoprobe: instance, game, profile all in the environment -> no refusal",
              f"exit {code}\n{out[-1500:]}")

        # K13 — the explicit, empty instance of K5 is a valid empty census: no switches. switchprobe must finish with
        # exit 0, zero switches, deepest chain 0, its closing lines and a complete --json, with the trigger map on
        # (the default) and off. The unchanged tree threw InvalidOperationException ("Sequence contains no elements")
        # from Max over the empty switch list after printing the load order (task 2026-10-07-switchprobe-empty-census).
        for trig in ("on", "off"):
            emit = tmp / f"census-{trig}.json"
            code, out = run("switchprobe", ["--instance", other_inst.as_posix(), "--game", other_game.as_posix(),
                                            "--profile", "OtherProf", "--triggers", trig, "--json", str(emit)], tmp)
            doc = None
            if emit.exists():
                try:
                    doc = json.loads(emit.read_text(encoding="utf-8"))
                except ValueError:
                    doc = None
            check(code == 0 and "Unhandled exception" not in out and "load order: 0 plugins" in out
                  and re.search(r"switches\s+:\s+0\b", out) is not None and "deepest chain 0;" in out
                  and "Nothing here is verified in game." in out and re.search(r"elapsed [\d.]+s", out) is not None,
                  f"K13 switchprobe --triggers {trig}: empty census -> exit 0, zero switches, deepest chain 0, complete output",
                  f"exit {code}\n{out[-2500:]}")
            check(doc is not None and doc["summary"]["switches"] == 0 and doc["switches"] == []
                  and doc["jointFlips"] == [] and doc["plugins"] == 0
                  and ((doc["summary"]["triggers"] is None) == (trig == "off")),
                  f"K13 switchprobe --triggers {trig}: empty census -> complete --json with zero switches",
                  f"json {'missing' if not emit.exists() else 'unreadable' if doc is None else json.dumps(doc)[:800]}")

        # K7 — a probe that requires its GAME before its instance (corridorprobe) and is given neither: the refusal
        # still names the missing instance, not the game. The unchanged tree read the config's game folder.
        code, out = run("corridorprobe", ["map", "SomeCell"], tmp)
        check(code == 2 and refusals_in(out) == ["instance-not-explicit"] and "HOUSECARL_GAME_PATH" in out,
              "K7 corridorprobe: game required first, no instance -> refused instance-not-explicit (and says how to name the game)",
              f"exit {code}\n{out[-1500:]}")

        # K9 — the instance alone: the run names its game and profile in the environment but no instance, and the
        # config names only an instance. Nothing is withheld, so this pins the instance getter itself (K1 is also
        # caught by the game/profile withholding). The unchanged tree read the config's instance.
        (tmp / "harness.json").write_text(json.dumps({"instance": cfg_inst.as_posix()}), encoding="utf-8")
        for probe in ("switchprobe", "geoprobe"):
            code, out = run(probe, [], tmp, {"HOUSECARL_GAME_PATH": other_game.as_posix(),
                                             "HOUSECARL_MO2_PROFILE": "CfgProf"})
            check(code == 2 and refusals_in(out) == ["instance-not-explicit"],
                  f"K9 {probe}: game and profile named, instance not -> refused instance-not-explicit",
                  f"exit {code}\n{out[-1500:]}")

        # K10 — HOUSECARL_MO2_INSTANCE counts only when set for THIS run. A value equal to the one persisted at User or
        # Machine level is a default by another name: refused instance-env-persistent. The persisted value is faked
        # through the HOUSECARL_HARNESS_TEST_PERSISTED_INSTANCE seam (it can only add a persisted value), so nothing is
        # written to the registry. The tree before this rule accepted it.
        run_env = {"HOUSECARL_MO2_INSTANCE": other_inst.as_posix(), "HOUSECARL_GAME_PATH": other_game.as_posix(),
                   "HOUSECARL_MO2_PROFILE": "OtherProf"}
        for level in ("User", "Machine"):
            code, out = run("geoprobe", [], tmp, {**run_env,
                                                  "HOUSECARL_HARNESS_TEST_PERSISTED_INSTANCE": f"{level}={other_inst.as_posix()}"})
            check(code == 2 and refusals_in(out) == ["instance-env-persistent"] and "--instance" in out,
                  f"K10 geoprobe: instance variable equals the {level}-level value -> refused instance-env-persistent",
                  f"exit {code}\n{out[-1500:]}")
        # K11 — the per-run handoff (evalrun's child_env) keeps working: a persisted value that differs from the run's.
        code, out = run("geoprobe", [], tmp, {**run_env,
                                              "HOUSECARL_HARNESS_TEST_PERSISTED_INSTANCE": f"User={cfg_inst.as_posix()}"})
        check(not refusals_in(out), "K11 geoprobe: run value differs from the persisted one -> accepted, no refusal",
              f"exit {code}\n{out[-1500:]}")
        # K12 — --instance on the command line wins over a persisted variable: no refusal.
        code, out = run("switchprobe", ["--instance", other_inst.as_posix(), "--game", other_game.as_posix(),
                                        "--profile", "OtherProf"], tmp,
                        {**run_env, "HOUSECARL_HARNESS_TEST_PERSISTED_INSTANCE": f"User={other_inst.as_posix()}"})
        check(not refusals_in(out) and "load order:" in out,
              "K12 switchprobe: --instance given, variable persisted -> flag used, no refusal", f"exit {code}\n{out[-1500:]}")

        # K8 — the config has no game at all and the explicit instance is the config's: nothing is withheld, so the
        # generic "not configured" guidance prints with no refusal reason (the rule changes nothing it does not own).
        (tmp / "harness.json").write_text(json.dumps({"instance": cfg_inst.as_posix(), "profile": "CfgProf"}),
                                          encoding="utf-8")
        code, out = run("switchprobe", ["--instance", cfg_inst.as_posix()], tmp)
        check(code == 2 and not refusals_in(out) and "No game directory configured" in out,
              "K8 switchprobe: config without game, its own instance -> generic 'not configured', no refusal reason",
              f"exit {code}\n{out[-1500:]}")

        # K6 — static: nothing in the probes reads the config's instance except HarnessTarget, so the rule above is
        # the only door. A second reader of "instance" from the JSON would bypass it.
        offenders = []
        for cs in HARNESS.rglob("*.cs"):
            if "bin" in cs.parts or "obj" in cs.parts or cs.name == "HarnessTarget.cs":
                continue
            text = cs.read_text(encoding="utf-8", errors="replace")
            if 'TryGetProperty("instance"' in text or "harness.local.json\")" in text:
                offenders.append(str(cs.relative_to(HARNESS)))
        check(not offenders, "K6 no probe source reads the config instance outside HarnessTarget", "\n".join(offenders))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
