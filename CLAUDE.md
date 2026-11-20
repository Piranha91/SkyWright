# SkyWright

SkyWright finds where Skyrim mods clash in the 3D world, shows each clash, and writes the modder's fixes into a patch plugin. It is a WPF app (`_geometry-harness/workbench`) over a run controller (`runcontrol`) and a set of probe programs, with houseCARL pinned as the submodule `external/houseCARL` for its core, its patch-writing server and its rulebook generator. Users start at [README.md](README.md).

## Rules

- **Probes stay separate programs,** so a crash stays isolated and Abort works per probe.
- **Each rule has one owner.** Never copy a rule from houseCARL or between C# and Python; call its owner. houseCARL's data (its skills' references, its run log) is read from the submodule, never copied in.
- **Do not edit `external/houseCARL`.** It is a pin. A core fix goes to houseCARL's own checkout; then the pin moves in one SkyWright commit.
- **Errors are one plain sentence: what went wrong and what to try.** No silently wrong answer and no silently degraded mode.
- **Tests:** proportional. Strict checks for file destinations, patch contents, undo, configuration identity and any "resolved" claim; UI-only work gets a build and a screenshot.
- **Wording** in the app uses the modder's words (load order, priority, mod, plugin), not internal terms.

## Build and check

```
git submodule update --init
dotnet build SkyWright.sln -c Release
dotnet run --project external/houseCARL/src/housecarl-generator -c Release --no-build -- external/houseCARL/generated external/houseCARL/generated/reference
_geometry-harness/runcontrol/bin/Release/net9.0/runcontrol.exe --self-test
dotnet test _geometry-harness/workbench.tests -c Release --no-build
```

The app and runcontrol find their pieces from the folder holding `SkyWright.sln`: one owner, `_geometry-harness/_shared/CheckoutLayout.cs`. The `WB_*` variables override it.

## Commits

One change per commit, a plain imperative subject under 72 characters. No publication, push or release without the owner's go.
