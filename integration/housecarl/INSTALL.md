# Using the houseCARL integration module from Claude Code

**Class: LIVING.** How a Claude Code session that has houseCARL loads this module's skills and model agents. The
module is a Claude Code plugin named `skywright`, in `integration/housecarl/claude-plugin/` of a SkyWright checkout:

```
claude-plugin/
  .claude-plugin/plugin.json      the manifest: name "skywright"
  skills/world-edit-compatibility/   run a world-edit compatibility evaluation with SkyWright
  skills/evaluation-reporting/       turn its bundle into a report and a narrated reel
  skills/driving-skywright/          drive SkyWright's app through its two workbench tools
  agents/model-claude-*.md           the agents an evaluation's models.* settings route a step to
```

houseCARL itself holds nothing of this: you opt in by loading this plugin beside houseCARL. The facts below are from
Claude Code's plugin documentation (the plugin manifest reference, "Add components to a plugin" and the plugin loading
reference, read 2026-11-25); each step names what it rests on.

## Steps

1. **Build SkyWright** (`powershell -File scripts\build.ps1` in the checkout). The skills run its Python tools and
   its Release build's programs; the module's own build (houseCARL's server, `winrt-tts`, `lookprobe`) is part of it.

2. **Start a session with the plugin loaded in place**, beside houseCARL as you normally run it:

   ```
   claude --plugin-dir <SkyWright checkout>\integration\housecarl\claude-plugin
   ```

   or put that folder in the `CLAUDE_CODE_PLUGIN_DIRS` variable. Such a plugin "loads for that session only", and
   "the directory loads in place and is never copied" (the loading reference), so nothing is installed and your
   settings are not changed. Do not load it under the name `housecarl`: a session-only plugin replaces an installed
   plugin of the same name; this one is named `skywright`, so your houseCARL plugin stays as it is.

3. **Do not install it by a copying route.** The skills find the SkyWright checkout as `${CLAUDE_PLUGIN_ROOT}/../../..`
   (the plugin folder is three levels below the checkout). Claude Code substitutes `${CLAUDE_PLUGIN_ROOT}` in a skill's
   body, and points it at the folder the plugin was loaded from; an install that copies the plugin into Claude Code's
   cache ("Files outside the plugin directory aren't copied") would point it at the copy, where there is no checkout.
   This version documents no permanent install.

4. **Check it loaded:** `claude --plugin-dir <...>\claude-plugin plugin list` shows `skywright@inline` under
   "Session-only plugins", and `claude --plugin-dir <...>\claude-plugin plugin details skywright` lists its three
   skills and three agents. In a session the skills are `/skywright:world-edit-compatibility`,
   `/skywright:evaluation-reporting` and `/skywright:driving-skywright` (or loaded by Claude when a request matches
   their descriptions), and the agents `skywright:model-claude-haiku-5-5`, `skywright:model-claude-sonnet-5-5` and
   `skywright:model-claude-opus-5-5`, the names `evalsettings.py route` gives.

`claude plugin validate <...>\claude-plugin --strict` checks the manifest and the skill and agent files.
