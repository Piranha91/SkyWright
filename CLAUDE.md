# SkyWright

SkyWright finds where Skyrim mods clash in the 3D world, shows each clash, and writes the modder's fixes into a patch plugin. It is a WPF app (`_geometry-harness/workbench`) over a run controller (`runcontrol`) and a set of probe programs that share an engine library (`src/SkyWright.Engine`), with houseCARL pinned as the submodule `external/houseCARL` for its core, its patch-writing server and its rulebook generator. Users start at [README.md](README.md).

## Rules

- **Probes stay separate programs,** so a crash stays isolated and Abort works per probe.
- **Each rule has one owner.** Never copy a rule between C# and Python or between SkyWright and houseCARL; call its owner. houseCARL's other data (its run log, its other skills' references) is read from the submodule, never copied in; the AI evaluation's own data (the report's words and scene policy, the model agents) is the module's, and SkyWright's grouping of review rows is `data/scene-policy.json`.
- **Code is placed by what it does, not by where it came from.** The focused app (the workbench, its 3D view, the detectors, runcontrol up to the verified bundle) is SkyWright's core. Tools for an AI that drives SkyWright (the report, reel, narration and capture Python, the in-game switch flips, `lookprobe`) live in the houseCARL integration module, `integration/housecarl/`. houseCARL gains nothing new from SkyWright.
- **The module is fenced.** It has its own folder and its own build output (`integration\housecarl\bin\<Configuration>\`), and it may use SkyWright; nothing in the app, the probes, runcontrol or the rest of the tree refers into it, and nothing anywhere reads houseCARL's `evaluation-reporting` or `world-edit-compatibility` skill folders or `plugin/agents`. The fence guard checks both; what is not yet closed is on its named lists with the step that removes each. A scan the app starts (runcontrol through the bundle) reads no evaluation settings file and starts no Python; an AI-driven evaluation of a lab entry is run by the module, which runs runcontrol for the measuring stages. "SkyWright runs without houseCARL" is about the app: no houseCARL program runs; houseCARL's core compiled in from the pin is fine, and the module needs houseCARL by design.
- **Do not edit `external/houseCARL`.** It is a pin. A fix to houseCARL's code goes to houseCARL's own checkout; then the pin moves in one SkyWright commit.
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
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe harness-suites
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe run-coverage
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe tool-index
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe fence
bin\Release\net9.0-windows10.0.19041.0\Tools\SkyWright.Guards\SkyWright.Guards.exe child-runner
dotnet test integration\housecarl\bin\Release\Tests\HousecarlIntegration.Tests\HousecarlIntegration.Tests.dll
```

The harness-suites guard runs every Python suite and probe self-test it finds (`--full` adds the slow ones); its `python` needs Pillow and numpy. `run-coverage` and `tool-index` run the pinned houseCARL generator over `docs/run-coverage.json`: a new probe or tool needs a row there, and `tool-index --write` regenerates `docs/TOOLS.md`.

`scripts\build.ps1` builds both configurations; build both after every change. Each is one folder, `bin\<Configuration>\net9.0-windows10.0.19041.0\`: `SkyWright.exe` (with houseCARL's code compiled in, `housecarl-mcp.dll`: the app starts no houseCARL program), `Probes\<name>\`, `Tools\runcontrol\`, `Data\` (with houseCARL's rulebook `corpus.json`), `Tests\` and the marker `skywright-layout.json`. `Directory.Build.props` owns that layout and writes the marker; programs read it through `_geometry-harness/_shared/CheckoutLayout.cs` and the Python tools through `_geometry-harness/buildlayout.py`. Never spell a program's path anywhere else. The houseCARL integration module builds from its own solution (`integration/housecarl/housecarl-integration.sln`, which `scripts\build.ps1` builds after the app) into `integration\housecarl\bin\<Configuration>\` (`Probes\lookprobe\`, `Tools\winrt-tts\`, `Tools\housecarl-mcp\` with houseCARL's server program and its rulebook, `Tests\`, marker `housecarl-integration-layout.json`), never into the app's folder; the app's build holds no houseCARL program. Its Python finds SkyWright, and the module's own build, through `integration/housecarl/skywright_paths.py`. The fence guard fails when anything outside the module refers into it.

Debug is the build to use by default; Release is for evaluations and anything timed. A rebuild replaces what a running SkyWright or evaluation in that checkout uses, so parallel sessions each work in their own git worktree (its own `bin\`), and merge `main` into their branch at each step unless that would interfere with their task, so the sessions do not drift apart.

## Commits

One change per commit, a plain imperative subject under 72 characters. No publication, push or release without the owner's go.
