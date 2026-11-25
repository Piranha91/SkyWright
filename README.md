# SkyWright

SkyWright finds where Skyrim mods clash in the 3D world and lets you look at each clash and fix it by hand. It reads a Mod Organizer 2 instance and profile, runs a set of probes over the load order (object overlap, placement contact, navmesh ownership, route cuts and others), shows each conflict in a 3D view, and writes your fixes into a patch plugin in an Output Folder you choose. The patch does nothing until you enable it in MO2.

SkyWright is in development. It is not released, and this repository is not yet public.

## Build and run

Requirements: Windows, the .NET 9 SDK, and git.

```
git clone --recurse-submodules <this repository>
powershell -File scripts\build.ps1
```

`scripts\build.ps1` makes houseCARL's record rulebook (`corpus.json`) when it is missing (`-Rulebook` remakes it), then builds the Debug and the Release configuration, each followed by the houseCARL integration module (below). Each build of the app is one folder at the top of the checkout:

```
bin\Debug\net9.0-windows10.0.19041.0\SkyWright.exe        (and bin\Release\... the same)
```

| Path in the build folder | What |
|---|---|
| `SkyWright.exe` | The app |
| `Probes\<name>\<name>.exe` | Every probe, and conflictdiff, navedit, ordermap and viewprobe |
| `Tools\runcontrol\runcontrol.exe` | The run controller |
| `Data\` | The check registry, the severity table, the rest of `data\`, houseCARL's rulebook (`corpus.json`) and its scene policy |
| `Tests\workbench.tests\` | The app's tests, which run against this build |
| `skywright-layout.json` | The marker: which configuration, and where each piece is |

The build holds no houseCARL program: the app and the run controller run houseCARL's code compiled in (`housecarl-mcp.dll` beside `SkyWright.exe` and runcontrol). houseCARL's server program belongs to the integration module, in `integration\housecarl\bin\<Configuration>\Tools\housecarl-mcp\` with its rulebook beside it.

Debug is for working on the app; Release is optimized, so evaluations and anything timed use it. `dotnet build SkyWright.sln -c Debug` (or `-c Release`) builds one configuration of the app alone.

**How each program finds its pieces.** First a `WB_*` override (`WB_CORPUS`, `WB_REGISTRY`, `WB_SEVERITY`, `WB_RUNCONTROL`, `WB_HARNESS`, `WB_SCENE_POLICY`, and `WB_NAVOWNPROBE`, `WB_CONFLICTDIFF`, `WB_PROBE_DIRS`), which the app names in its log when it starts. Then the build the program sits in: its own folder (the app) or two folders up (a probe, a tool), where `skywright-layout.json` is. There is no other search, so pieces of two builds never mix unless an override names one. A rebuild replaces the programs of that build, so do not rebuild a checkout while it runs an evaluation; parallel work uses its own git worktree, which has its own `bin\`.

Set `WB_MCP_PORT` to a port number to serve the app's state tools (`workbench_state`, `workbench_act`) on `http://127.0.0.1:<port>/`.

## Layout

| Path | What |
|---|---|
| `_geometry-harness/workbench/` | The app (`SkyWright.exe`) |
| `_geometry-harness/runcontrol/` | The run controller: census through bundle |
| `_geometry-harness/*probe/`, `_shared/` | The probes and the code they share |
| `_geometry-harness/viewprobe/` | The renderer host the 3D view uses (on SkyRender) |
| `_geometry-harness/*.py`, `tests/`, `planeassess/` | Detector development tools, the record capture and bundle enrichment, and their tests; not shipped in the app. They find built programs through `buildlayout.py` |
| `integration/housecarl/` | The houseCARL integration module: tools for an AI that drives SkyWright (the report, reel, narration and capture Python, the in-game switch flips, `lookprobe`) and their tests. It uses SkyWright; nothing outside it refers into it (the `fence` guard). Its own solution builds to `integration\housecarl\bin\<Configuration>\` (with houseCARL's server program, built from the pin, in `Tools\housecarl-mcp\`), never into the app's build folder, and its Python finds SkyWright and the module's own build through `skywright_paths.py` |
| `src/SkyWright.Engine/`, `src/SkyWright.Engine.Tests/` | The engine the probes share (geometry, contact, navmesh, Havok, reach), on houseCARL's core, and its tests |
| `src/SkyWright.Guards/` | Checks a build and the unit tests do not reach: `harness-suites` runs the Python suites and the probes' self-tests (the module's too); `run-coverage` and `tool-index` check `docs/run-coverage.json` and `docs/TOOLS.md` through houseCARL's generator; `fence` checks that nothing outside `integration/housecarl/` refers into it |
| `data/` | The check registry, the severity table, the record field scope and the overlap triage policy |
| `external/houseCARL/` | houseCARL, pinned as a git submodule: its core, server (which writes patches) and generator |
| `scripts/build.ps1` | Builds the rulebook when missing, then Debug and Release, each with the integration module |
| `scripts/runlog.py` | Loads houseCARL's run log from the submodule |
| `docs/clash-list-format.md` | The clash list format: clashes an AI, a script or a person writes for SkyWright to list and show |

## Checks

```
powershell -File scripts\build.ps1
bin\Release\net9.0-windows10.0.19041.0\Tools\runcontrol\runcontrol.exe --self-test
dotnet test bin\Release\net9.0-windows10.0.19041.0\Tests\workbench.tests\workbench.tests.dll
dotnet test bin\Release\net9.0-windows10.0.19041.0\Tests\SkyWright.Engine.Tests\SkyWright.Engine.Tests.dll
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe harness-suites
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe run-coverage
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe tool-index
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe fence
dotnet test integration\housecarl\bin\Release\Tests\HousecarlIntegration.Tests\HousecarlIntegration.Tests.dll
```

Twelve workbench tests read real evaluation output and fail without the environment variables that point at it.

## Credits and license

SkyWright began inside [houseCARL](https://github.com/Avick3110/houseCARL), by Aaron (Avick3110), and reuses its code with his agreement. It is built on [Mutagen](https://github.com/Mutagen-Modding/Mutagen) and [SkyRender](https://www.nuget.org/packages/SkyRender). SkyWright is licensed under the GNU General Public License, version 3.0 only (`LICENSE`); see `THIRD-PARTY-NOTICES.md`. Where its history came from: `HISTORY.md`.
