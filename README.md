# SkyWright

SkyWright finds where Skyrim mods clash in the 3D world and lets you look at each clash and fix it by hand. It reads a Mod Organizer 2 instance and profile, runs a set of probes over the load order (object overlap, placement contact, navmesh ownership, route cuts and others), shows each conflict in a 3D view, and writes your fixes into a patch plugin in an Output Folder you choose. The patch does nothing until you enable it in MO2.

SkyWright is in development. It is not released, and this repository is not yet public.

## Build and run

Requirements: Windows, the .NET 9 SDK, and git.

```
git clone --recurse-submodules <this repository>
dotnet build SkyWright.sln -c Release
dotnet run --project external/houseCARL/src/housecarl-generator -c Release --no-build -- external/houseCARL/generated external/houseCARL/generated/reference
```

The last line makes the record rulebook (`corpus.json`) that houseCARL's server and the run controller read. Then start the app:

```
_geometry-harness/workbench/bin/Release/net9.0-windows10.0.19041.0/workbench.exe
```

Set `WB_MCP_PORT` to a port number to serve the app's state tools (`workbench_state`, `workbench_act`) on `http://127.0.0.1:<port>/`.

## Layout

| Path | What |
|---|---|
| `_geometry-harness/workbench/` | The app |
| `_geometry-harness/runcontrol/` | The run controller: census through bundle |
| `_geometry-harness/*probe/`, `_shared/` | The probes and the code they share |
| `_geometry-harness/viewprobe/` | The renderer host the 3D view uses (on SkyRender) |
| `_geometry-harness/*.py`, `tests/`, `planeassess/` | Pipeline, reporting and video tools and their tests; not shipped in the app |
| `data/` | The check registry, the severity table, the record field scope and the overlap triage policy |
| `external/houseCARL/` | houseCARL, pinned as a git submodule: its core, server (which writes patches) and generator |
| `scripts/runlog.py` | Loads houseCARL's run log from the submodule |

## Checks

```
dotnet build SkyWright.sln -c Release
_geometry-harness/runcontrol/bin/Release/net9.0/runcontrol.exe --self-test
dotnet test _geometry-harness/workbench.tests -c Release --no-build
```

Eleven workbench tests read real evaluation output and fail without the environment variables that point at it.

## Credits and license

SkyWright began inside [houseCARL](https://github.com/Avick3110/houseCARL), by Aaron (Avick3110), and reuses its code with his agreement. It is built on [Mutagen](https://github.com/Mutagen-Modding/Mutagen) and [SkyRender](https://www.nuget.org/packages/SkyRender). SkyWright is licensed under the GNU General Public License, version 3.0 only (`LICENSE`); see `THIRD-PARTY-NOTICES.md`. Where its history came from: `HISTORY.md`.
