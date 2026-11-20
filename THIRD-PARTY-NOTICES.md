# Third-party notices

SkyWright is licensed under the GNU General Public License, version 3.0 only (GPL-3.0-only); the full text is in `LICENSE`.

## houseCARL

SkyWright's probes, run controller and app were written inside houseCARL and reuse its code: the core library (records, load order, assets, writes), the server that writes patches, and the generator that makes the record rulebook. houseCARL is pinned here as the submodule `external/houseCARL`.

- Author: Aaron (Avick3110), who agreed to this reuse on 2026-11-17.
- Source: https://github.com/Avick3110/houseCARL
- License: GPL-3.0-only. Its own notices are `external/houseCARL/THIRD-PARTY-NOTICES.txt` and travel with it.

## Libraries

| Component | Used by | License | Source |
|---|---|---|---|
| Mutagen (`Mutagen.Bethesda.Skyrim`) | houseCARL core, probes | GPL-3.0-only | https://github.com/Mutagen-Modding/Mutagen |
| SkyRender, SkyRender.Character | viewprobe, the app's 3D view | GPL-3.0 | https://www.nuget.org/packages/SkyRender |
| Nifly (houseCARL's NiflySharp fork) | houseCARL core, niflyprobe | GPL-3.0-only | https://github.com/Avick3110/NiflySharp (package in `external/houseCARL/packages/local`) |
| ModelContextProtocol | the app's state tools | as its package states | https://www.nuget.org/packages/ModelContextProtocol.Core |
| SixLabors.ImageSharp, ImageSharp.Drawing | viewprobe | as its package states | https://www.nuget.org/packages/SixLabors.ImageSharp |
| Pfim | contactprobe, proofprobe | as its package states | https://www.nuget.org/packages/Pfim |

This table is written by hand for development. Before any release, it is replaced by a generated list of every shipped component with its license text, as houseCARL's `scripts/generate-notices.ps1` does for houseCARL.
