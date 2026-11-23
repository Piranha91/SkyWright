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
_geometry-harness/workbench/bin/Release/net9.0-windows10.0.19041.0/SkyWright.exe
```

Set `WB_MCP_PORT` to a port number to serve the app's state tools (`workbench_state`, `workbench_act`) on `http://127.0.0.1:<port>/`.

## Install a commit

The checkout is for development: a build there replaces the programs in its own `bin` folders. To run evaluations, or to use the app while you keep building, publish a commit into its own folder:

```
dotnet run --project _geometry-harness/publish -c Release -- [--commit REV] [--to DIR]
```

It refuses a checkout with uncommitted changes. It exports the commit and its pinned houseCARL with `git archive`, builds that copy in `artifacts/install-src/` (deleted afterwards), and publishes into `DIR/<commit>/`, by default `artifacts/install/<commit>/` in the checkout. `artifacts/` is ignored by git, and a build of the solution never writes there. `git clean -x` deletes it, installs included.

| Path in the install | What |
|---|---|
| `skywright-install.json` | The manifest: the commit, the houseCARL pin, the named pieces and every file's sha256 |
| `SkyWright/SkyWright.exe` | The app, with the same `viewprobe.exe` the install's `viewprobe/` holds |
| `runcontrol/`, `<probe>/` | The run controller and every probe, each in its own folder |
| `housecarl-mcp/` | houseCARL's server, built from the pin, with the rulebook `corpus.json` beside it |
| `housecarl/` | houseCARL's other data the app reads (the reporting skill's scene policy) |
| `data/` | The check registry, the severity table and the rest of `data/` |

Check an install against its manifest with `dotnet run --project _geometry-harness/publish -c Release -- --verify DIR/<commit>`.

The install needs the .NET 9 runtime (Desktop Runtime for the app). It needs no SDK, no Python and no houseCARL.

**How each program finds its pieces.** First a `WB_*` override (`WB_SERVER`, `WB_CORPUS`, `WB_REGISTRY`, `WB_SEVERITY`, `WB_RUNCONTROL`, `WB_HARNESS`, `WB_SCENE_POLICY`), which the app names in its log when it starts. Then the install the program sits in, when its folder's parent holds `skywright-install.json`. Last, the checkout above it (the folder holding `SkyWright.sln`). A program in an install never falls back to a checkout. A damaged install (a listed piece missing, or the folder not one of its programs) is refused in one sentence. The lab entry's record capture is a Python step, so it runs only from a checkout's runcontrol.

## Layout

| Path | What |
|---|---|
| `_geometry-harness/workbench/` | The app (`SkyWright.exe`) |
| `_geometry-harness/runcontrol/` | The run controller: census through bundle |
| `_geometry-harness/*probe/`, `_shared/` | The probes and the code they share |
| `_geometry-harness/viewprobe/` | The renderer host the 3D view uses (on SkyRender) |
| `_geometry-harness/publish/` | `skywright-publish`: builds an install from a commit (not itself installed) |
| `_geometry-harness/*.py`, `tests/`, `planeassess/` | Pipeline, reporting and video tools and their tests; not shipped in the app |
| `data/` | The check registry, the severity table, the record field scope and the overlap triage policy |
| `external/houseCARL/` | houseCARL, pinned as a git submodule: its core, server (which writes patches) and generator |
| `scripts/runlog.py` | Loads houseCARL's run log from the submodule |
| `docs/clash-list-format.md` | The clash list format: clashes an AI, a script or a person writes for SkyWright to list and show |

## Checks

```
dotnet build SkyWright.sln -c Release
_geometry-harness/runcontrol/bin/Release/net9.0/runcontrol.exe --self-test
dotnet test _geometry-harness/workbench.tests -c Release --no-build
```

Eleven workbench tests read real evaluation output and fail without the environment variables that point at it.

## Credits and license

SkyWright began inside [houseCARL](https://github.com/Avick3110/houseCARL), by Aaron (Avick3110), and reuses its code with his agreement. It is built on [Mutagen](https://github.com/Mutagen-Modding/Mutagen) and [SkyRender](https://www.nuget.org/packages/SkyRender). SkyWright is licensed under the GNU General Public License, version 3.0 only (`LICENSE`); see `THIRD-PARTY-NOTICES.md`. Where its history came from: `HISTORY.md`.
