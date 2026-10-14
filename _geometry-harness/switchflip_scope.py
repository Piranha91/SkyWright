"""switchflip_scope — which switches the runtime state-switch check (`runtime.state-switch-flip`) is about, on one arm.

A library, not an entry point: evalrun (the not-applicable answer at run/resume), evallive (the import refusal) and
flipplan (the generator's population) call `population`; the caller owns the run.

The census (switchprobe --json, the arm's `state.switch-census` output) covers the whole load order, so the base game's
own scripted switches are always in it. The check is about the switches that touch the evaluated targets:

  a switch is in the population when a script drives it (`trigger.status` Triggered, or BoundUnread: bound, with the
  call not read) AND it touches a target:
    - by plugin: its owner, or a member's placer or attacher, is a target plugin (scope `targets[].name`);
    - by cell: its cell, or a member's cell, is a cell a target places in (scope `footprint.placed.cells[].cell`,
      matched to the census `cellKey`; both are FormKey text).

`not-applicable` only when nothing leaves the answer unknown and the population is empty. Unknown never becomes
not-applicable; each cause that keeps it open is named:
  no-trigger-map      the census ran without the trigger map (`summary.triggers` absent or null);
  unreadable-plugins  the census could not read some plugins, so their switches are absent, not zero;
  scope-incomplete    the scope is not complete;
  no-targets          the scope names no target plugin;
  cells-unknown       a script-driven switch does not touch a target by plugin, and some target's placed cells are not
                      listed, so whether it touches by cell is not known.
"""
from __future__ import annotations

SCRIPT_DRIVEN = ("Triggered", "BoundUnread")


def _members(s: dict):
    for side in ("onSet", "offSet"):
        for m in s.get(side) or []:
            if isinstance(m, dict):
                yield m


def _targets(scope: dict) -> tuple[set[str], set[str], list[str]]:
    """(target plugin names lowercased, cells targets place in, targets whose placed cells are not listed)."""
    names, cells, unlisted = set(), set(), []
    for t in scope.get("targets") or []:
        if not isinstance(t, dict) or not t.get("name"):
            continue
        names.add(str(t["name"]).lower())
        placed = ((t.get("footprint") or {}).get("placed") or {})
        rows = placed.get("cells")
        if not isinstance(rows, list):
            unlisted.append(str(t["name"]))
            continue
        cells.update(str(c.get("cell")) for c in rows if isinstance(c, dict) and c.get("cell"))
    return names, cells, unlisted


def touches(s: dict, names: set[str], cells: set[str]) -> str | None:
    """'plugin', 'cell' or None: how the switch touches a target."""
    plugins = [s.get("owner")] + [m.get(k) for m in _members(s) for k in ("placer", "attacher")]
    if any(p and str(p).lower() in names for p in plugins):
        return "plugin"
    keys = [s.get("cellKey")] + [m.get("cellKey") for m in _members(s)]
    if any(k and str(k) in cells for k in keys):
        return "cell"
    return None


def population(census: dict, scope: dict) -> dict:
    """{status: not-applicable | open, causes: [{code, detail}], population: [{key, edid, trigger, by}], counts}."""
    causes = []
    summary = census.get("summary") or {}
    if not isinstance(summary.get("triggers"), dict):
        causes.append({"code": "no-trigger-map",
                       "detail": "the census ran without the trigger map, so which switches a script drives is unknown"})
    unreadable = list(census.get("unreadable") or [])
    if unreadable:
        causes.append({"code": "unreadable-plugins",
                       "detail": f"the census could not read {len(unreadable)} plugin(s) ({', '.join(map(str, unreadable[:5]))}), "
                                 "so their switches are absent, not zero"})
    if scope.get("complete") is not True:
        causes.append({"code": "scope-incomplete", "detail": "the scope is not complete, so the targets are not known"})
    names, cells, unlisted = _targets(scope)
    if not names:
        causes.append({"code": "no-targets", "detail": "the scope names no target plugin"})

    switches = [s for s in census.get("switches") or [] if isinstance(s, dict)]
    driven = [s for s in switches if ((s.get("trigger") or {}).get("status")) in SCRIPT_DRIVEN]
    pop, undecided = [], []
    for s in driven:
        by = touches(s, names, cells)
        if by:
            pop.append({"key": s.get("key"), "edid": s.get("edid"), "trigger": (s.get("trigger") or {}).get("status"),
                        "by": by})
        elif unlisted:
            undecided.append(s.get("key"))
    if undecided:
        causes.append({"code": "cells-unknown",
                       "detail": f"{len(undecided)} script-driven switch(es) touch no target plugin, and the scope lists no "
                                 f"placed cells for {', '.join(unlisted)}, so whether they lie in a target's cells is unknown"})
    counts = {"switches": len(switches), "scriptDriven": len(driven),
              "triggered": sum(1 for s in driven if s["trigger"]["status"] == "Triggered"),
              "boundUnread": sum(1 for s in driven if s["trigger"]["status"] == "BoundUnread"),
              "population": len(pop), "undecided": len(undecided)}
    status = "not-applicable" if not causes and not pop else "open"
    return {"status": status, "causes": causes, "population": pop, "counts": counts}


def sentence(result: dict) -> str:
    """One sentence for the ledger: why the arm is not applicable, or what keeps it open."""
    c = result["counts"]
    if result["status"] == "not-applicable":
        return (f"the census on this arm lists {c['switches']} switch(es); none that a quest or script drives touches a "
                f"target by plugin or by cell ({c['triggered']} triggered and {c['boundUnread']} bound-but-unread "
                "elsewhere), so there is nothing to flip")
    if result["population"]:
        return (f"{c['population']} script-driven switch(es) touch a target on this arm "
                f"({c['triggered']} triggered, {c['boundUnread']} bound-but-unread in the whole census)")
    return "; ".join(x["detail"] for x in result["causes"])
