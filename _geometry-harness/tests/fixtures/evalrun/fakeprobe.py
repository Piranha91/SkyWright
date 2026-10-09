"""The fixture probe engine — a fake detector that writes the REAL conflict-file format.

One engine, one thin wrapper per probe name under `fakeprobes/`. It accepts the flag shape the real
probes accept (`--order FILE --emit FILE [--base FILE] [--json FILE] [--out DIR] [--instance --profile
--game] [extra]`) and takes its behaviour from a scenario JSON whose path is in `EVALRUN_FAKE_SCENARIO`:

    { "pluginDir": "...",                      # default: ../plugins beside the scenario file
      "population": "load-time", "state": "baseline",
      "arms": { "<probe>": { "<emit stem>": { "behaviour": "ok", "rows": [...], "coverage": [...],
                                              "predicates": {"<FormKey>": "Gone"}, ... } } } }

The emit stem is the `--emit` filename without `.json` — `baseline`, `candidate-01-reorder`, and for
ordersweep's reverse arm `candidate-01-reorder.reverse`. `"*"` is the fallback entry for a probe.

What is REAL here and not faked:
  - the file shape is ConflictReport.cs's (`Conflicts[{Kind, A, B, Summary, Necessary[...]}]`, `Coverage`,
    `Predicates`, `BaseEmit`, `Population`, `State`, `Configuration`), so the real conflictdiff.exe reads it;
  - the Configuration fingerprint is ConfigurationIdentity.Compute's canonical text over the fixture
    plugins' actual sha256s, so two arms that consumed different plugin content get different fingerprints
    by the same rule the C# uses.

Behaviours (PLAN.md's fixture list): ok, exit, refuse, illegal, stale-output, no-emit, wrong-probe-name,
no-configuration, wrong-state, extraction-failed, crash-after.

A scenario may also carry `identityDamage`: a list of rules that mutate ONE thing about an otherwise
canonical Configuration, keyed on the emit stem (one part of one arm) or the order stem (every
emit-writing probe run over one candidate file). Kinds: wrong-order, wrong-hash, bad-fingerprint,
empty-identity, wrong-reference-order, wrong-copy. See `damage_identity` (and `resolve_name` for
wrong-copy, which is applied while the order is resolved rather than after).

RESOLVING A PLUGIN THE pluginDir DOES NOT HOLD (Q44 / D1). A candidate may name a plugin that is not
in `pluginDir` — a supplied patch. It is then resolved through `--instance/--profile/--game` (or
HOUSECARL_MO2_INSTANCE / _PROFILE / HOUSECARL_GAME_PATH, which every child also receives) by the
probes' OWN precedence, written here from `_shared/HarnessOrderCli.cs` (`BuildLayerMap`, `ProviderOf`
and the out-of-view LIMIT sentence) and `src/housecarl-core/Mo2LoadOrder.cs` (`ParseModlist`) — NOT
from evalrun.py, which implements the same definition independently. The five layers, first sighting
wins, names compared case-insensitively, `.esp/.esm/.esl` at a folder's top level only, an absent or
unreadable folder skipped:

    1 <instance>/overwrite
    2 the view's mod folders in priority order (the candidate's `Mods` if it has one, else the
      scope's `configuration.providers` of kind `mod` in their recorded order)
    3 <game>/Data
    4 the profile's enabled mods (`+` lines of modlist.txt, file order) that are not in the view
    5 the profile's disabled mods (`-` lines, file order)

`Provider` is the layer's name (`overwrite`, the mod folder's own name, `Data`). A plugin resolved
from layer 4 or 5 adds the C#'s out-of-view sentence to `Configuration.Limits`, one line per folder.
When an instance is configured and NO layer provides the name, this refuses with the C#'s wording and
exits 2 without writing the emit. With no instance configured the pluginDir IS the layer map and a
missing file keeps its zero-hash row — case 13's `plugin-gone` step rests on exactly that.
"""
from __future__ import annotations

import hashlib
import json
import os
import sys
import time
from pathlib import Path

SCHEMA = 1
# overlapprobe's emits carry a measurement policy; conflictdiff refuses overlap repair credit without one (3c9432e3)
FIXTURE_OVERLAP_POLICY = "fixture-overlap-v1"
DEFAULT_UNKNOWN = {
    "installerChoices": "not modelled: MO2 keeps no per-mod record of the FOMOD options taken; the installed files are the evidence",
    "gameBuild": "not read: the executable version and SKSE/plugin set are not part of a records run",
    "distributors": "not read: SPID / KID / SkyPatcher layers are not scanned; only BOS ini files are listed as overlays",
    "saveState": "not applicable: a records run judges a fresh load, never a save",
}


def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fingerprint(plugins, providers, overlays, population, state) -> str:
    """ConfigurationIdentity.Compute's canonical text, line for line: schema, population, state, one
    `plugin:<lowercase name>:<sha256>` per plugin IN ORDER, one `provider:<kind>:<lowercase name>` per
    provider, one `overlay:<kind>:<sha256>` per overlay; every line newline-terminated; sha256 hex lower."""
    sb = [f"schema:{SCHEMA}\n", f"population:{population}\n", f"state:{state}\n"]
    for p in plugins:
        sb.append(f"plugin:{p['Name'].lower()}:{p['Sha256']}\n")
    for v in providers:
        sb.append(f"provider:{v['Kind']}:{v['Name'].lower()}\n")
    for o in overlays:
        sb.append(f"overlay:{o['Kind']}:{o['Sha256']}\n")
    return hashlib.sha256("".join(sb).encode("utf-8")).hexdigest()


PLUGIN_EXTS = (".esp", ".esm", ".esl")


class Unresolved(Exception):
    """No layer provides a name the candidate lists. The C# refuses the whole order for this."""


def parse_modlist(path: Path):
    """Mo2LoadOrder.ParseModlist: `+Name` enabled, `-Name` disabled, `#` a comment, a `…_separator`
    name skipped from BOTH lists, every other line ignored; FILE ORDER, top line highest priority.
    Returns (enabled, disabled, readable) — an unreadable modlist is not an empty one."""
    p = Path(path)
    if not p.is_file():
        return [], [], False
    try:
        lines = p.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return [], [], False
    enabled, disabled = [], []
    for raw in lines:
        line = raw.rstrip()
        if not line or line[0] == "#":
            continue
        marker = line[0]
        if marker not in "+-":
            continue
        name = line[1:].strip()
        if not name or name.lower().endswith("_separator"):
            continue
        (enabled if marker == "+" else disabled).append(name)
    return enabled, disabled, True


def resolve_ctx(a: dict, scen: dict, candidate: dict) -> dict:
    """The instance the layers are walked in. The flags come first; a probe run without them finds the
    same instance through HarnessTarget's environment variables, so the fallback is not a guess."""
    instance = a.get("instance") or os.environ.get("HOUSECARL_MO2_INSTANCE")
    profile = a.get("profile") or os.environ.get("HOUSECARL_MO2_PROFILE")
    game = a.get("game") or os.environ.get("HOUSECARL_GAME_PATH")
    # HarnessOrderCli.cs:147 is `cand.Mods ?? comp.EnabledMods`: an EMPTY Mods list is a real empty
    # view — the candidate stages no mod folders — and only a NULL or absent one falls back. A
    # truthiness test here silently turns the first into the second.
    view = candidate.get("Mods", candidate.get("mods"))
    if view is None:
        view = []
        scope_path = scen.get("scopeFrom")
        if scope_path and Path(scope_path).is_file():
            try:
                doc = json.loads(Path(scope_path).read_text(encoding="utf-8"))
            except ValueError:
                doc = {}
            cfg = doc.get("configuration") or doc.get("Configuration") or {}
            for v in cfg.get("providers", cfg.get("Providers", [])) or []:
                if str(v.get("kind", v.get("Kind", ""))).lower() == "mod":
                    view.append(str(v.get("name", v.get("Name"))))
    return {"instance": instance, "profile": profile, "game": game, "view": list(view)}


def build_layer_map(ctx: dict) -> dict:
    """HarnessOrderCli.BuildLayerMap: lowercase filename -> (layer name, path), first sighting wins."""
    inst = ctx.get("instance")
    if not inst:
        return {}
    root = Path(inst)
    mods = root / "mods"
    layers = [("overwrite", root / "overwrite", "view")]
    view = list(ctx.get("view") or [])
    for m in view:
        layers.append((m, mods / m, "view"))
    if ctx.get("game"):
        layers.append(("Data", Path(ctx["game"]) / "Data", "view"))
    enabled, disabled, _ = parse_modlist(root / "profiles" / str(ctx.get("profile") or "") / "modlist.txt")
    in_view = {m.lower() for m in view}
    for m in enabled:
        if m.lower() not in in_view:
            layers.append((m, mods / m, "out-of-view"))
    for m in disabled:
        if m.lower() not in in_view:
            layers.append((m, mods / m, "out-of-view"))

    out = {}
    for layer, d, kind in layers:
        try:
            entries = sorted(Path(d).iterdir())
        except OSError:
            continue                                   # an absent or unreadable folder is skipped
        for f in entries:
            if not f.is_file() or f.suffix.lower() not in PLUGIN_EXTS:
                continue
            out.setdefault(f.name.lower(), (layer, f, kind))
    return out


def resolve_name(name: str, plugin_dir: Path, ctx: dict | None, wrong_copy: dict | None):
    """(path, provider, kind) for one plugin name. The pluginDir copy is the fixture's own in-view
    layer; anything else is resolved through the instance. `wrong_copy` reads the named folder's copy
    instead of the precedence winner — the fixture's way of being a probe that read another copy."""
    if wrong_copy and str(wrong_copy.get("plugin", "")).lower() == name.lower():
        folder = str(wrong_copy.get("folder") or "")
        inst = (ctx or {}).get("instance")
        if not inst:
            raise Unresolved(f"identityDamage wrong-copy needs an instance to read '{folder}' from")
        src = Path(inst) / "mods" / folder / name
        if not src.is_file():
            raise Unresolved(f"identityDamage wrong-copy names '{folder}', which holds no {name}")
        return src, folder, "out-of-view"
    local = plugin_dir / name
    if local.exists():
        return local, "fixture-mods", "view"
    hit = build_layer_map(ctx or {}).get(name.lower())
    if hit:
        layer, path, kind = hit
        return path, layer, kind
    if (ctx or {}).get("instance"):
        raise Unresolved(name)
    return local, "fixture-mods", "view"               # no instance: today's zero-hash row


def plugin_rows(names, plugin_dir: Path, ctx: dict | None = None, wrong_copy: dict | None = None) -> list:
    rows = []
    for name in names:
        path, provider, _kind = resolve_name(name, plugin_dir, ctx, wrong_copy)
        ext = Path(name).suffix.lower()
        rows.append({
            "Name": name, "Path": str(path).replace("\\", "/"),
            "Sha256": sha256_file(path) if path.exists() else "0" * 64,
            "Bytes": path.stat().st_size if path.exists() else 0,
            "Provider": provider, "MasterFlag": ext in (".esm", ".esl"),
            "LightFlag": ext == ".esl", "Masters": [],
        })
    return rows


def out_of_view_limits(rows, ctx: dict | None) -> list:
    """HarnessOrderCli's sentence, one line per offending FOLDER naming its plugins — seventy
    near-identical lines would bury the one that matters."""
    in_view = {m.lower() for m in (ctx or {}).get("view") or []}
    by_folder = {}
    for r in rows:
        p = str(r.get("Provider") or "")
        if p in ("overwrite", "Data") or p.lower() in in_view:
            continue
        by_folder.setdefault(p, []).append(r["Name"])
    return [f"{len(names)} plugin(s) were resolved from the folder '{folder}', which is NOT in this "
            "configuration's asset view: their RECORDS load, but that folder's meshes, textures, "
            "scripts and BSAs are not in the view, and the copy read there may not be the copy the "
            "game would load. A geometry or script result that depends on them is not established "
            f"here. [{', '.join(names)}]"
            for folder, names in by_folder.items()]


def refusal_text(name: str) -> str:
    return (f"candidate names '{name}', but no mod folder, the overwrite layer or the game Data "
            "folder provides a file by that name. A missing plugin is a DIFFERENT order, not this one.")


def identity_rule(scen: dict, order_stem: str, emit_stem: str) -> dict | None:
    """The scenario's `identityDamage` rules, matched on the EMIT stem first (which names one part of one
    arm — `candidate-01-reorder.reverse`) then on the ORDER stem (which names every emit-writing probe run
    over one candidate file, the shape R1 actually had). A rule keyed on the order stem therefore damages
    the whole arm, and the within-arm consistency rules cannot mask the request rule under test."""
    for rule in scen.get("identityDamage") or []:
        if rule.get("emitStem") and rule["emitStem"] == emit_stem:
            return rule
        if rule.get("orderStem") and rule["orderStem"] == order_stem:
            return rule
    return None


def damage_identity(cfg: dict, rule: dict, plugin_dir: Path) -> dict:
    """Mutate ONE thing about a Configuration and leave everything else canonical, so exactly one of
    D2's rules can fire. The Fingerprint is recomputed over the mutated content unless the rule pins it
    (which is how `bad-fingerprint` is the only case where the digest itself is wrong)."""
    kind = rule.get("kind")
    if kind == "wrong-order":
        cfg["Plugins"] = list(reversed(cfg["Plugins"]))
    elif kind == "wrong-hash":
        want = str(rule.get("plugin") or "FixtureTarget.esp").lower()
        hit = [p for p in cfg["Plugins"] if str(p["Name"]).lower() == want]
        if len(hit) != 1:
            print(f"fakeprobe: identityDamage wrong-hash names {want!r}, which appears {len(hit)} time(s) "
                  f"in this emit's Plugins: the fixture would sabotage nothing", file=sys.stderr)
            return None
        h = str(hit[0]["Sha256"])
        hit[0]["Sha256"] = ("1" if h[:1] != "1" else "2") + h[1:]
    elif kind == "empty-identity":
        cfg["Plugins"] = []
    elif kind == "wrong-reference-order":
        cfg["Plugins"] = plugin_rows(rule.get("plugins") or [], plugin_dir)
    elif kind == "bad-fingerprint":
        pass
    else:
        print(f"fakeprobe: unknown identityDamage kind {kind!r}", file=sys.stderr)
        return None
    pinned = rule.get("fingerprint")
    cfg["Fingerprint"] = pinned if pinned else fingerprint(
        cfg["Plugins"], cfg["Providers"], cfg["Overlays"], cfg["Population"], cfg["State"])
    print(f"fakeprobe: identityDamage {kind} applied ({len(cfg['Plugins'])} plugin(s), "
          f"fingerprint {cfg['Fingerprint'][:12]})")
    return cfg


def configuration(candidate: dict, candidate_path: Path, plugin_dir: Path, population: str, state: str, limits,
                  ctx: dict | None = None, wrong_copy: dict | None = None) -> dict:
    plugins = plugin_rows(candidate.get("Plugins") or candidate.get("plugins") or [], plugin_dir,
                          ctx, wrong_copy)
    # HarnessOrderCli.Resolve: `overwrite`, then the view's mod folders in order, then `Data`. A candidate
    # that declares `Mods` (an empty list included) reads THAT view; one that does not keeps the fixture
    # scope's single mod folder, which is every case's scope but case 18's.
    declared = candidate.get("Mods", candidate.get("mods"))
    view = [str(m) for m in declared] if isinstance(declared, list) else ["fixture-mods"]
    providers = ([{"Name": "overwrite", "Kind": "overwrite"}]
                 + [{"Name": m, "Kind": "mod"} for m in view]
                 + [{"Name": "Data", "Kind": "data"}])
    overlays = []
    return {
        "SchemaVersion": SCHEMA,
        "Fingerprint": fingerprint(plugins, providers, overlays, population, state),
        "Source": f"candidate:{candidate_path.name}",
        "Plugins": plugins, "Providers": providers, "Overlays": overlays,
        "Population": population, "State": state,
        "Limits": list(limits or []) + out_of_view_limits(plugins, ctx),
        "Unknown": dict(DEFAULT_UNKNOWN),
    }


def row_identity(kind: str, form_key: str, other, a: str) -> str:
    kind, form_key, a = kind.lower(), form_key.lower(), a.lower()
    other = other.lower() if other else None
    if other is None:
        return f"{kind}|{form_key}|{a}"
    return f"{kind}|{form_key}|{other}" if form_key <= other else f"{kind}|{other}|{form_key}"


def conflicts_from(rows) -> list:
    out = []
    for r in rows:
        out.append({
            "Kind": r.get("kind", "CLOBBERED"),
            "A": r.get("a", "FixtureTarget.esp"),
            "B": r.get("b", "FixtureBase.esp"),
            "Summary": r.get("summary", "fixture row"),
            "Necessary": [{"FormKey": r["formKey"], "Role": r.get("role", "navmesh"),
                           "Label": r.get("label", "fixture cell"),
                           "Physical": r.get("physical", "Holds"),
                           "PhysicalDetail": r.get("detail", "fixture predicate")}],
        })
    return out


def predicates_from(base_emit: Path, entry: dict) -> list:
    """Every row of the base emit, re-measured under THIS arm — what a real probe's `--base` writes."""
    doc = json.loads(base_emit.read_text(encoding="utf-8"))
    table = entry.get("predicates") or {}
    default = entry.get("predicateDefault", "Holds")
    out = []
    for c in doc.get("Conflicts", []):
        alt = c.get("Alternative") or []
        other = alt[0]["FormKey"] if alt else None
        for n in c.get("Necessary", []):
            out.append({"Kind": c["Kind"], "A": c["A"], "B": c["B"], "FormKey": n["FormKey"], "Other": other,
                        "Physical": table.get(n["FormKey"], default),
                        "Detail": "fixture re-measurement under this arm"})
    return out



def default_json(probe: str, entry: dict, scen: dict, order_name: str) -> dict:
    """What the REAL probe writes under --json, in the shape the registry's `adapter.result` spec reads:
    switchprobe findings=summary.switches / judged=plugins / unjudgeable=unreadable; bosprobe findings=rows
    / judged=plugins. A fake that writes its own shape is rejected schema-unsupported, correctly."""
    n = int(entry.get("plugins", 4))
    if probe == "switchprobe":
        rows = entry.get("switches", [{"switch": "0A0010:FixtureTarget.esp", "kind": "quest-stage",
                                       "children": 2, "set": "on"}] * int(entry.get("switchCount", 3)))
        return {"generated": time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime()),
                "instance": scen.get("instance", "fixture://evalrun"), "profile": order_name,
                "plugins": n, "unreadable": list(entry.get("unreadable", [])),
                "summary": {"switches": len(rows), "children": 2 * len(rows), "descendants": 2 * len(rows),
                            "onSet": len(rows), "offSet": 0, "chained": 0, "cycles": 0, "playerParentRefs": 0},
                "perPlugin": [], "switches": rows, "jointFlips": []}
    if probe == "bosprobe":
        return {"profile": order_name, "plugins": n, "inis": list(entry.get("inis", [])),
                "rows": list(entry.get("rows", []))}
    if probe == "adviceprobe":
        # The controller hands adviceprobe a --json sink beside the registry's --out, and reads the
        # result paths (rules / pagesRead / warnings) off it, so the same document goes to both.
        return advice_report(entry, str(entry.get("modId", "45686")))
    if probe == "scopeprobe":
        scope_path = scen.get("scopeFrom")
        if scope_path and Path(scope_path).exists():
            doc = json.loads(Path(scope_path).read_text(encoding="utf-8"))
            doc["generatedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
            return doc
    return {"probe": probe, "plugins": n, "rows": list(entry.get("rows", []))}


def advice_report(entry: dict, mod_id: str) -> dict:
    """adviceprobe's report-<id>.json: the registry reads findings=rules, judged=pagesRead,
    unjudgeable=warnings, so all three keys are present and countable."""
    return {"modId": mod_id,
            "rules": list(entry.get("rules", [{"before": "FixtureTarget.esp", "after": "FixtureBase.esp",
                                               "hardness": "advice", "source": "stored page",
                                               "resolutionChain": ["the page states a load order"]}])),
            "resolutions": [], "notes": ["a fixture report; no page was fetched"],
            "staleness": ["the stored copy's age is not the date this report was assembled"],
            "noOrderStated": False,
            "pagesRead": list(entry.get("pagesRead", [{"url": "stored://fixture/page", "from": "snapshot"}])),
            "catalogue": {"dir": "fixture", "refresh": "offline", "maxRequests": 0, "requestsAttempted": 0,
                          "requestsSucceeded": 0, "waitedMs": 0, "pagesServedFromSnapshot": 1,
                          "pagesFetched": 0, "pagesReplayedFromArchive": 0, "imports": []},
            "warnings": list(entry.get("warnings", [])), "choices": []}


def parse(argv):
    a = {"extra": [], "target": []}
    i = 0
    while i < len(argv):
        t = argv[i]
        if t in ("--order", "--emit", "--base", "--json", "--out", "--instance", "--profile", "--game",
                 "--state", "--target", "--mod", "--page") and i + 1 < len(argv):
            if t == "--target":
                a["target"].append(argv[i + 1])
            else:
                a[t[2:]] = argv[i + 1]
            i += 2
            continue
        a["extra"].append(t)
        i += 1
    return a


def main(probe: str, argv=None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    a = parse(argv)
    spath = os.environ.get("EVALRUN_FAKE_SCENARIO")
    if not spath:
        print("fakeprobe: EVALRUN_FAKE_SCENARIO is not set", file=sys.stderr)
        return 2
    scen = json.loads(Path(spath).read_text(encoding="utf-8"))
    plugin_dir = Path(scen.get("pluginDir") or (Path(spath).resolve().parent.parent / "plugins"))

    emit = Path(a["emit"]) if a.get("emit") else None
    stem = emit.name[:-5] if emit and emit.name.endswith(".json") else (emit.stem if emit else "-")
    arms = (scen.get("arms") or {}).get(probe, {})
    entry = arms.get(stem) or arms.get("*") or {}
    behaviour = entry.get("behaviour", "ok")
    population = entry.get("population", scen.get("population", "load-time"))
    state = entry.get("state", scen.get("state", "baseline"))

    print(f"fakeprobe {probe}: arm '{stem}' behaviour '{behaviour}'")

    # THE INTERRUPTION SEAM. A test that kills the controller on a timer kills it at a varying point, and
    # sometimes nothing is in flight when it dies. So the probe the test names blocks until the test removes
    # a sentinel file: the controller is then provably mid-check, and the kill lands where the test means it
    # to. Nothing else changes - this probe is an ordinary `ok` arm once the sentinel is gone.
    sentinel = os.environ.get("EVALRUN_FAKE_SENTINEL")
    if sentinel and os.environ.get("EVALRUN_FAKE_BLOCK_PROBE") == probe:
        print(f"fakeprobe {probe}: blocking while {sentinel} exists", flush=True)
        waited = 0.0
        while Path(sentinel).exists() and waited < 120:
            time.sleep(0.05)
            waited += 0.05
        print(f"fakeprobe {probe}: sentinel gone after {waited:.2f}s", flush=True)

    # ---- the non-emitting adapters (--json / --out): scopeprobe, switchprobe, adviceprobe, patchprobe, driftprobe
    if a.get("json") and not emit:
        if behaviour == "no-emit":
            return 0
        if behaviour == "refuse":
            print("REFUSED: fixture refusal", file=sys.stderr)
            return 1
        order_name = Path(a["order"]).stem if a.get("order") else "fixture"
        doc = dict(entry.get("json") or default_json(probe, entry, scen, order_name))
        p = Path(a["json"])
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        return int(entry.get("code", 0)) if behaviour == "exit" else 0
    if a.get("out") and not emit:
        d = Path(a["out"])
        d.mkdir(parents=True, exist_ok=True)
        # The real probe names the file after the mod id it was ASKED for (--mod), so the fake must too:
        # a report-<other id>.json is a file the reader cannot find, and it is rejected for that.
        arg = a.get("mod")
        mod = str(arg) if arg and not str(arg).startswith("{") else str(entry.get("modId", "45686"))
        (d / f"report-{mod}.json").write_text(
            json.dumps(entry.get("report") or advice_report(entry, mod), indent=1), encoding="utf-8")
        (d / f"rules-{mod}.json").write_text(json.dumps(entry.get("rules") or {"SchemaVersion": 1, "Rules": []}, indent=1), encoding="utf-8")
        return 0

    if probe == "patchprobe" and emit is None:
        # A `text` adapter: the registry reads patchprobe's counts off its printed lines
        # (`indexed N plugins (U unreadable)`, `M distinct master pairs`), so that is all it prints.
        if behaviour == "exit":
            return int(entry.get("code", 4))
        print(f"fakeprobe patchprobe: indexed {int(entry.get('plugins', 4))} plugins "
              f"({int(entry.get('unreadableCount', 0))} unreadable)")
        print(f"{int(entry.get('masterPairs', 0))} distinct master pairs")
        return 0
    if emit is None:
        print("fakeprobe: no --emit and no --json/--out", file=sys.stderr)
        return 2

    # ---- refusals, before anything is written
    if behaviour == "refuse":
        print("REFUSED: the fixture scenario says this arm refuses")
        return int(entry.get("code", 1))
    if behaviour == "illegal":
        print("REFUSED: MISSING MASTER — the candidate loads a plugin before its master; the game would not load it")
        return 2
    if behaviour == "no-emit":
        print("fakeprobe: exiting 0 without writing the emit (the orchestration case)")
        return 0

    order = Path(a["order"]) if a.get("order") else None
    candidate = json.loads(order.read_text(encoding="utf-8")) if order else {"Plugins": []}
    limits = candidate.get("Limits") or []
    rule = identity_rule(scen, order.stem if order else "-", stem)
    ctx = resolve_ctx(a, scen, candidate)
    # wrong-copy is a RESOLUTION-time damage: the probe reads another folder's copy, so the path, the
    # hash, the provider, the out-of-view limit and the fingerprint all follow from what it read.
    wrong_copy = rule if rule and rule.get("kind") == "wrong-copy" else None
    try:
        cfg = configuration(candidate, order, plugin_dir, population, state, limits, ctx, wrong_copy)
    except Unresolved as ex:
        name = str(ex)
        print("REFUSED: " + (refusal_text(name) if " " not in name else name), file=sys.stderr)
        return 2
    if wrong_copy:
        row = next((p for p in cfg["Plugins"] if p["Name"].lower() == str(wrong_copy["plugin"]).lower()), None)
        print(f"fakeprobe: identityDamage wrong-copy read {wrong_copy.get('folder')}'s copy of "
              f"{wrong_copy.get('plugin')} (sha {str((row or {}).get('Sha256'))[:12]})")
    elif rule:
        cfg = damage_identity(cfg, rule, plugin_dir)
        if cfg is None:                       # a sabotage that would not sabotage is a broken fixture
            return 2

    rows = entry.get("rows", [])
    coverage = entry.get("coverage")
    if behaviour == "extraction-failed":
        rows, coverage = [], [{"Population": entry.get("coveragePopulation", "reference pairs"), "Judged": 0,
                               "Unjudgeable": {"GeometryUnreadable": int(entry.get("unjudgeable", 17))}}]
    if coverage is None:
        coverage = [{"Population": entry.get("coveragePopulation", "fixture population"),
                     "Judged": int(entry.get("judged", 42)), "Unjudgeable": {}}]

    doc = {
        "Probe": entry.get("probeName", probe) if behaviour == "wrong-probe-name" else probe,
        "Instance": scen.get("instance", "fixture://evalrun"),
        "GeneratedUtc": time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime()),
        "Conflicts": conflicts_from(rows),
        "Coverage": coverage,
        "Population": population,
        "State": entry.get("stateName", "night") if behaviour == "wrong-state" else state,
    }
    if a.get("base"):
        base = Path(a["base"])
        if base.exists():
            doc["Predicates"] = predicates_from(base, entry)
            doc["BaseEmit"] = str(base).replace("\\", "/")
    if behaviour == "wrong-state":
        cfg["State"] = doc["State"]
        cfg["Fingerprint"] = fingerprint(cfg["Plugins"], cfg["Providers"], cfg["Overlays"], cfg["Population"], cfg["State"])
    if behaviour != "no-configuration":
        doc["Configuration"] = cfg
    # an entry's `measurementPolicy` overrides the default; None leaves the field out, as an emit before 3c9432e3 did
    policy = entry.get("measurementPolicy", FIXTURE_OVERLAP_POLICY if probe == "overlapprobe" else None)
    if policy is not None:
        doc["MeasurementPolicy"] = policy
    doc["PopulationIdentity"] = f"{doc['Population']} / state {doc['State']}"

    text = json.dumps(doc, indent=1)
    emit.parent.mkdir(parents=True, exist_ok=True)

    if behaviour == "crash-after":
        n = int(entry.get("bytes", max(1, len(text) // 3)))
        with open(emit, "w", encoding="utf-8") as f:
            f.write(text[:n])
            f.flush()
        print("fakeprobe: exiting mid-write")
        sys.stdout.flush()
        os._exit(int(entry.get("code", 1)))

    emit.write_text(text, encoding="utf-8")

    if behaviour == "stale-output":
        # A zero exit over an output that is not this run's: the GeneratedUtc and the mtime both predate the child.
        stale = entry.get("staleUtc", "2026-09-01 00:00:00Z")
        doc["GeneratedUtc"] = stale
        emit.write_text(json.dumps(doc, indent=1), encoding="utf-8")
        old = time.mktime(time.strptime(stale, "%Y-%m-%d %H:%M:%SZ"))
        os.utime(emit, (old, old))
        print(f"fakeprobe: wrote a STALE emit (GeneratedUtc {stale}) and exited 0")
        return 0

    if behaviour == "exit":
        return int(entry.get("code", 4))
    return 0
