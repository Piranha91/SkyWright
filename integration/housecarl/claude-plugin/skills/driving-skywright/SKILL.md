---
name: driving-skywright
description: "Drive SkyWright's desktop app (the conflict workbench) through its two endpoint tools, workbench_state and workbench_act: start it with its endpoint, open an evaluation bundle on the load order it was measured on, read the ranked clashes, look at one in 3D, stage and preview a fix, write it to a new patch plugin, and undo it. Use when asked to look at, explain or fix a SkyWright clash in the app, or to check what the app shows."
---

# Driving SkyWright's app

SkyWright's app (`SkyWright.exe`, the conflict workbench) shows the clashes an evaluation bundle found in a 3D view of
the load order, and writes a modder's fixes into a new patch plugin. It serves exactly two tools to an LLM:
`workbench_state` (read) and `workbench_act` (do one thing, as the window's own buttons do). This skill says how to
reach them and what a session looks like. The tool-by-tool reference, with every argument and refusal, is
[references/workbench-tools.md](references/workbench-tools.md). `SW` is the SkyWright checkout this plugin was loaded
from: `${CLAUDE_PLUGIN_ROOT}/../../..` (the plugin is `SW/integration/housecarl/claude-plugin/`, loaded in place).

## 1. Start the app with its endpoint

The endpoint is off unless asked for. Start the app with a port:

```
SW\bin\Release\net9.0-windows10.0.19041.0\SkyWright.exe --mcp-port 8770
```

(or set `WB_MCP_PORT=8770`; `--mcp-port` exists for launchers that pass arguments but not environment variables). There
is no default port and no discovery file: you choose the port, and the app says where it listens in its log
(`workbench.log` in `%TEMP%\workbench\`, or in the folder `WB_LOG_DIR` names; `workbench-<process id>.log` there when a
second app holds the first file):

```
[wb] MCP state tools (workbench_state, workbench_act) served at http://127.0.0.1:8770/ (Streamable HTTP)
```

The endpoint is `http://127.0.0.1:<port>/`: loopback only, MCP over Streamable HTTP, stateless, no token. If the port
is taken, the log says `MCP state tools not started on port <port>: <reason>` and the app runs without the endpoint;
pick another port and restart it. Another SkyWright may already be open on this machine: never stop it, and never take
its port.

Every answer is one text block: on success indented JSON, on a refusal one sentence (the call is marked as an error)
saying what was wrong and what to try. Each call runs on the window's thread; if the window is busy (rebuilding a cell,
or another long step) for more than 15 seconds, the call is withdrawn with one sentence asking you to call again.

## 2. A session

1. **See where it stands:** `workbench_state` with `project: ["launcher"]` (works before anything is open).
2. **Open the load order** the bundle was measured on: `workbench_act` `{verb: "open_profile", mode: "free",
   instance: "<MO2 instance folder>", profile: "<profile>", game: "<game folder>"}` (the game folder only when the
   instance's ModOrganizer.ini names none).
3. **Choose the Output Folder before opening the bundle** (`{verb: "set_output", path: "<folder>"}`): a patch is written
   into a new mod folder there (or into the instance's own `mods` folder). The open scene keeps the write target it had
   when it opened, so an Output Folder chosen after the bundle opened does not reach it: writes are refused until the
   bundle is opened again.
4. **Open the bundle:** `{verb: "open", path: "<bundle folder>"}` (the folder with `manifest.json`, `plan.json` and
   `receipts/`, or one above it with `evidence/` or `bundle/evidence/`). The app checks it with `runcontrol
   verify-bundle` and loads the first clash's cell; `workbench_state` `["bundle"]` then shows its plan id, the arm, the
   verify line and what it could not show.
5. **Read the clashes:** `workbench_state` `{project: ["conflicts"], limit: 20, offset: 0}` lists them ranked: kind,
   the two plugins, the two references with their base EditorIDs, the cell, a one-line summary, severity and your
   decision so far. `["scenes"]` groups them by cell and cluster; `["findings"]` lists the other checks' rows.
6. **Look at one:** `{verb: "select", conflict: <rank>}` opens it and lands the camera on it. Then `workbench_state`
   `["selection", "context"]`: where the reference is drawn, where the load order puts it and where a patch put it;
   the doors near it, what names it, its enable parent, what rests on it, the navmesh under it, and what stays behind
   if it moves. `{verb: "view", op: "isolate"}` hides everything around the pair; `{verb: "view", op: "restore"}` brings
   it back; `{verb: "capture_window", path: "<file>.png"}` saves the window.
7. **Stage a fix:** `{verb: "stage", delta: [dx, dy, dz]}` (Skyrim units, about 70 to a metre; +X east, +Y north, +Z
   up), `turn_degrees`, an absolute `position`/`rotation_degrees`, or a typed `offset` such as `"+120 x"`; `{verb:
   "drop"}` lowers the selected reference onto what is drawn under it. `workbench_state` `["pending"]` shows each staged
   edit in words ("Moved RTKeepGate 200 units south").
8. **Preview, then write:** `{verb: "commit", patch: "WbFixGate"}` previews only (`dry_run` is true by default): which
   patch would be written, its masters, each edit, and what stays behind. `{verb: "commit", patch: "WbFixGate",
   dry_run: false}` writes a new patch plugin (a name no mod folder holds yet) and, when a clash is open, starts its
   re-check (overlap or contact measured again with the patch, about 70 to 80 seconds): read it with `workbench_state`
   `{project: ["recheck"], wait_seconds: 150}`.
9. **Undo:** `{verb: "undo"}` takes back the last staged edit; a written edit is taken back with `{verb: "revert",
   ref: "<XXXXXX:Plugin.esp>"}`, which removes it from its patch. `{verb: "decide", conflict: <rank>, verdict: "real
   problem" | "intentional" | "unsure", reason: "..."}` records your judgement and opens the next undecided clash.

The app never enables a patch: the load order is unchanged until the user enables the patch in Mod Organizer 2. A scene
opened from the Results or World tab is read-only: the edit verbs are refused there.
