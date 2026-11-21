# Clash list format

A clash list is a file of clashes that SkyWright's own checks did not write. An AI, a script or a person can write one. SkyWright loads it with **Load › Clash List…** and lists its clashes under **Conflicts › In the world**. A clash that names a record or a cell opens in the 3D view.

The file name must end in `.clashes.json`, for example `riften-mods.clashes.json`.

## Example

```json
{
  "format": "skywright-clash-list",
  "version": 1,
  "title": "Riften mods",
  "loadOrder": "X:/Skyrim Conflict Lab/profiles/WB-largest",
  "clashes": [
    {
      "mods": ["Riften Expansion.esp", "Unofficial Skyrim Special Edition Patch.esp"],
      "what": "A market stall sits inside a tree the patch moved.",
      "record": "000891:Skyrim.esm",
      "severity": "high"
    },
    {
      "mods": ["Mod A", "Mod B"],
      "what": "Both mods edit the same cell's lighting.",
      "cell": "00BCF0:Skyrim.esm"
    }
  ]
}
```

## Fields

| Field | Required | What it is |
|---|---|---|
| `format` | yes | Always `"skywright-clash-list"`. |
| `version` | yes | Always `1`. |
| `title` | no | A name for the list, shown above its clashes. |
| `loadOrder` | no | The load order the clashes are in: an MO2 profile folder, an MO2 instance folder, or a game or Data folder. A relative path is read from the clash list's folder. SkyWright loads it so the clashes can open in the view. Without it, they open on whatever load order is already loaded. |
| `clashes` | yes | The clashes, in the order to list them. It may be empty. |

Each clash:

| Field | Required | What it is |
|---|---|---|
| `mods` | yes | The two or more mods or plugins the clash is between, by name. |
| `what` | yes | One sentence saying what clashes. |
| `record` | no | The placed object (or other record) to show, as `FormID:plugin`, such as `000891:Skyrim.esm`. The FormID is the record's ID without the load-order prefix. |
| `cell` | no | The cell to show when there is no single record, in the same form. |
| `severity` | no | Your own word for how bad it is (`high`, `medium`, `low`), shown as given. |

A clash with neither `record` nor `cell` is listed, but it does not open in the view.

SkyWright checks the whole file when it loads it. Anything wrong is refused in one sentence that names the clash and the field, and nothing from the file is listed.
