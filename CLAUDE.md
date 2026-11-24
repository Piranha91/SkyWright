# SkyWright

SkyWright finds where Skyrim mods clash in the 3D world, shows each clash, and writes the modder's fixes into a patch plugin. It is a WPF app (`_geometry-harness/workbench`) over a run controller (`runcontrol`) and a set of probe programs that share an engine library (`src/SkyWright.Engine`), with houseCARL pinned as the submodule `external/houseCARL` for its core, its patch-writing server and its rulebook generator. Users start at [README.md](README.md).

## Rules

- **Probes stay separate programs,** so a crash stays isolated and Abort works per probe.
- **Each rule has one owner.** Never copy a rule between C# and Python or between SkyWright and houseCARL; call its owner. houseCARL's data (its skills' references, its run log) is read from the submodule, never copied in.
- **Upstream houseCARL is houseCARL's; home-made code SkyWright may own.** Code that comes from upstream houseCARL (`Avick3110/houseCARL`, `origin/main` in houseCARL's checkout) stays there and is called through the pin. Code that exists only in Piranha's houseCARL branches, never upstream, SkyWright may claim: it moves into SkyWright in one change and SkyWright owns it from then on. If houseCARL's own code still calls it, ask before moving it.
- **Do not edit `external/houseCARL`.** It is a pin. A fix to upstream code goes to houseCARL's own checkout; then the pin moves in one SkyWright commit. Home-made code is claimed (above), not edited in place.
- **Errors are one plain sentence: what went wrong and what to try.** No silently wrong answer and no silently degraded mode.
- **Tests:** proportional. Strict checks for file destinations, patch contents, undo, configuration identity and any "resolved" claim; UI-only work gets a build and a screenshot.
- **Wording** in the app uses the modder's words (load order, priority, mod, plugin), not internal terms.

## Build and check

```
git submodule update --init
powershell -File scripts\build.ps1
bin\Release\net9.0-windows10.0.19041.0\Tools\runcontrol\runcontrol.exe --self-test
dotnet test bin\Release\net9.0-windows10.0.19041.0\Tests\workbench.tests\workbench.tests.dll
dotnet test bin\Release\net9.0-windows10.0.19041.0\Tests\SkyWright.Engine.Tests\SkyWright.Engine.Tests.dll
```

`scripts\build.ps1` builds both configurations; build both after every change. Each is one folder, `bin\<Configuration>\net9.0-windows10.0.19041.0\`: `SkyWright.exe`, `Probes\<name>\`, `Tools\runcontrol\`, `Tools\housecarl-mcp\` (with the rulebook), `Data\`, `Tests\` and the marker `skywright-layout.json`. `Directory.Build.props` owns that layout and writes the marker; programs read it through `_geometry-harness/_shared/CheckoutLayout.cs` and the Python tools through `_geometry-harness/buildlayout.py`. Never spell a program's path anywhere else.

Debug is the build to use by default; Release is for evaluations and anything timed. A rebuild replaces what a running SkyWright or evaluation in that checkout uses, so parallel sessions each work in their own git worktree (its own `bin\`), and merge `main` into their branch at each step unless that would interfere with their task, so the sessions do not drift apart.

## Commits

One change per commit, a plain imperative subject under 72 characters. No publication, push or release without the owner's go.
