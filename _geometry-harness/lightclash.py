"""Light-clash fold over lightprobe outputs (lane L, 2026-10-04).

Given lightprobe JSON for the combined order (u), each mod alone (plusa, plusb), the dependencies alone (dep) and the patched
order (p), decides for each patch-edited light whether the combination of the two mods produces a lighting problem that
neither produces alone, and what the patch edit does about it. Classes (engine basis in the lane's Gate A):
  C1   orphaned light: in its own mod's arrangement a fixture (candle, fire, window, ...) lies within H of the light; in the
       combination that fixture is gone or moved beyond H. C1b, the mirror: a fixture its own arrangement lights has no
       enabled light within H in the combination. A light with no fixture within H is a fill light: C1 does not apply.
  C2   wrong side of the surfaces: most first hits from the light (64 rays inside its radius) are BACK faces, attributed by
       the four-setup rule. "void": no surface of either mod within its radius (it lights nothing). The leak magnitude
       (C2c) is printed, never decides: the calibration found 74.8 % of vanilla non-shadow lights above its frozen threshold
       (clutter between a light and its walls counts as leak).
  C3a  doubled fixture: one fixture within H of enabled lights from both mods.
  C4   per-shape limit (vanilla renderer: > 7 lights or > 4 shadow lights on one drawn shape; Light Limit Fix reading: > 7
       portal-strict lights): COUNT ONLY, never decides (vanilla interiors exceed it in 7 of 40 calibration cells; whether
       the engine shows it is an in-game question).
Attribution (four-setup): positive in u and in no single-mod or dependency-only arrangement where the light is present.
Verdicts per edit: fixes an inter-mod clash / patch light clash / taste (no clash) / cannot tell (with the class and reason).
A DISABLE that reads taste while both mods' lights reach each other says so ("both mods' lights enabled together: N lights,
brightness not judged").

usage: python lightclash.py --arm u=U.json --arm plusa=A.json --arm plusb=B.json --arm dep=D.json --arm p=P.json
                                 --mods A.esp B.esp --edits EDITS.json --out OUT.json [--host-radius 64]
EDITS.json: [{"id": ..., "action": "DISABLE"|"ADD"|"MOVE"|..., "records": ["XXXXXX:Plugin.esp", ...]}, ...]
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sys
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

POLICY = "light_clash_fold_v1"
FIXTURE = re.compile(r"candle|fire|brazier|lantern|chandelier|sconce|torch|hearth|lamp|window|forge|oven|cookpot", re.I)
NOT_FIXTURE = re.compile(r"firewood|firefly|fireflies", re.I)
SINGLES = ("plusa", "plusb", "dep")
VANILLA_LIMIT, VANILLA_SHADOW_LIMIT, STRICT_LIMIT = 7, 4, 7


@dataclass(frozen=True)
class Params:
    host_radius: float
    leak_threshold: float


def _on(r):
    return bool(r.get("enabled")) and r.get("resolved", True) and not r.get("deleted", False)


def is_fixture(r):
    if r.get("type") == "Light":
        return False
    s = (r.get("edid") or "") + " " + (r.get("mesh") or "")
    return bool(FIXTURE.search(s)) and not NOT_FIXTURE.search(s)


def _fixtures_near(arr, pos, h):
    out = []
    for r in arr["refs"].values():
        if _on(r) and is_fixture(r):
            d = math.dist(pos, r["pos"])
            if d <= h:
                out.append((d, r["key"]))
    return sorted(out)


def c1_orphan(light_key, own, u, p: Params):
    l_own = own["lights"].get(light_key)
    if l_own is None or not l_own.get("enabled"):
        return {"applies": False, "positive": False, "label": "not lit in its own arrangement"}
    hosts = _fixtures_near(own, l_own["pos"], p.host_radius)
    if not hosts:
        return {"applies": False, "positive": False, "label": "fill light (no fixture within H)"}
    host = hosts[0][1]
    l_u = u["lights"].get(light_key)
    if l_u is None or not l_u.get("enabled"):
        return {"applies": True, "positive": False, "host": host, "label": "not lit in the combination"}
    h = u["refs"].get(host)
    if h is not None and _on(h) and math.dist(h["pos"], l_u["pos"]) <= p.host_radius:
        return {"applies": True, "positive": False, "host": host, "distU": math.dist(h["pos"], l_u["pos"])}
    dist = math.dist(h["pos"], l_u["pos"]) if h is not None and _on(h) else None
    return {"applies": True, "positive": True, "host": host, "distU": dist,
            "label": "host " + ("gone" if dist is None else f"{dist:.0f} u away") + " in the combination"}


def c1b_unlit(fixture_key, own, u, p: Params):
    f_own = own["refs"].get(fixture_key)
    if f_own is None or not _on(f_own):
        return {"applies": False, "positive": False}
    lit_own = [l for l in own["lights"].values() if l.get("enabled") and math.dist(l["pos"], f_own["pos"]) <= p.host_radius]
    if not lit_own:
        return {"applies": False, "positive": False, "label": "not lit in its own arrangement"}
    f_u = u["refs"].get(fixture_key)
    if f_u is None or not _on(f_u):
        return {"applies": False, "positive": False, "label": "fixture absent in the combination"}
    lit_u = [l["key"] for l in u["lights"].values() if l.get("enabled") and math.dist(l["pos"], f_u["pos"]) <= p.host_radius]
    return {"applies": True, "positive": not lit_u, "litBy": lit_u}


def mod_of(key, arr, mods):
    plugin = key.split(":", 1)[1] if ":" in key else ""
    if plugin in mods:
        return plugin
    r = arr["refs"].get(key) or {}
    return r.get("responsible") if r.get("responsible") in mods else None


def c3a_doubled(fixture_key, arr, mods, p: Params):
    f = arr["refs"].get(fixture_key)
    if f is None or not _on(f):
        return {"applies": False, "positive": None}
    lights = [l["key"] for l in arr["lights"].values() if l.get("enabled") and math.dist(l["pos"], f["pos"]) <= p.host_radius]
    owners = {mod_of(k, arr, mods) for k in lights} - {None}
    return {"applies": True, "positive": len(owners) >= 2, "lights": lights, "owners": sorted(owners)}


def c2_leak(light_u, light_own, p: Params):
    """The leak magnitude in u against the light's own arrangements (C2c). Printed only; never decides (see the module doc)."""
    if light_u.get("shadow"):
        return {"applies": False, "positive": False, "label": "shadow light: its shadow map occludes it"}
    if "leak" not in light_u:
        return {"applies": True, "positive": None, "label": "not measured"}
    lu = light_u["leak"]["leak"]
    base = max([o["leak"]["leak"] for o in light_own if o and "leak" in o] or [0.0])
    excess = lu - base
    pos = excess > p.leak_threshold
    if not pos and light_u.get("incomplete"):
        return {"applies": True, "positive": None, "excess": excess, "label": "incomplete: " + ", ".join(light_u["incomplete"])}
    return {"applies": True, "positive": pos, "excess": excess}


def c2_facing(light):
    """Raw per-arrangement reading: True when most first hits are back faces; None when not lit or not measured."""
    if light is None or not light.get("enabled") or "facing" not in light:
        return None
    f = light["facing"]
    if f["back"] > f["front"]:
        return True
    if light.get("incomplete"):
        return None
    return False


def is_void(light):
    if light is None or "facing" not in light:
        return False
    f, k = light["facing"], light.get("leak", {})
    return f["front"] + f["back"] == 0 and k.get("lit", 0) + k.get("leak", 0) == 0


def attribute(values):
    u = values.get("u")
    if u is None:
        return "not measured in u"
    if u is False:
        return "no clash in u"
    if any(values.get(a) is True for a in SINGLES):
        return "not the pair"
    return "needs both"


def c4_limit(shape_key, arms):
    out = {}
    for reading in ("vanilla", "llf"):
        vals = {}
        for a in ("u",) + SINGLES:
            s = arms.get(a, {}).get("shapes", {}).get(tuple(shape_key)) if a in arms else None
            if s is None:
                vals[a] = None
            elif reading == "vanilla":
                vals[a] = s["count"] > VANILLA_LIMIT or s.get("shadow", 0) > VANILLA_SHADOW_LIMIT
            else:
                vals[a] = s.get("strict", 0) > STRICT_LIMIT
        out[reading] = {"positiveU": vals["u"], "attribution": attribute(vals)}
    out["disagree"] = out["vanilla"]["positiveU"] != out["llf"]["positiveU"]
    return out


def placement_kind(base_type, edid):
    if base_type == "Light":
        return "light"
    if base_type == "Activator" and edid and "TRIG" in edid.upper():
        return "trigger"
    if base_type == "Container":
        return "reach"
    return "stand"


def floor_rule_applies(kind):
    return kind != "light"


DECIDING = ("C1", "C1b", "C2", "C3a")


def verdict(action, classes_u, patched, doubling):
    made = [c for c, v in patched.items() if v.get("positiveP")]
    if made:
        return {"verdict": "patch light clash", "text": "the patched order shows " + ", ".join(sorted(made)), "roomDoubling": False}
    fixed = [c for c, v in classes_u.items() if v.get("attribution") == "needs both" and c in DECIDING]
    fixed += [c for c, v in patched.items() if v.get("fixedInP")]
    if fixed:
        return {"verdict": "fixes an inter-mod clash", "text": "the combination alone shows " + ", ".join(sorted(set(fixed))) + "; the edit removes it",
                "roomDoubling": False}
    open_ = [(c, v) for c, v in classes_u.items() if c in DECIDING and v.get("attribution") == "not measured in u" and v.get("applies", True)]
    if open_:
        return {"verdict": "cannot tell", "text": "; ".join(f"{c} not measured in u ({v.get('label', 'no reading')})" for c, v in open_),
                "roomDoubling": False}
    text = "every applicable class read no clash only the combination makes"
    if doubling:
        return {"verdict": "taste (no clash)", "roomDoubling": True,
                "text": f"both mods' lights enabled together: {doubling['lights']} lights, brightness not judged; " + text}
    return {"verdict": "taste (no clash)", "text": text, "roomDoubling": False}


# ---------------------------------------------------------------- the fold over lightprobe outputs

def load_arm(path):
    d = json.load(open(path, encoding="utf-8"))
    arr = {"refs": {}, "lights": {}, "shapes": {}, "cellOf": {}}
    for c in d["cells"]:
        for r in c["refs"]:
            arr["refs"][r["key"]] = r
            arr["cellOf"][r["key"]] = c["edid"]
        for l in c["lights"]:
            arr["lights"][l["key"]] = l
        for s in c.get("shapes") or []:
            arr["shapes"][(s["ref"], s["shape"])] = s
    return arr


def own_arm(key, arms, mods):
    m = mod_of(key, arms["u"] if key in arms["u"]["refs"] else arms["p"], mods)
    if m == mods[0]:
        return "plusa"
    if m == mods[1]:
        return "plusb"
    return "dep"


def judge_record(key, action, arms, mods, p: Params):
    """One edited record -> its class readings and verdict."""
    u, pa = arms["u"], arms["p"]
    lu, lp = u["lights"].get(key), pa["lights"].get(key)
    if lu is None and lp is None:
        return {"key": key, "light": False}
    own_name = own_arm(key, arms, mods)
    own = arms[own_name]
    classes, patched, printed = {}, {}, {}
    if lu is not None and lu.get("enabled"):
        # C1 (orphan), in each arrangement against the light's own one
        c1 = {a: (c1_orphan(key, own, arms[a], p) if key in arms[a]["lights"] else None) for a in ("u",) + SINGLES}
        if c1["u"]["applies"]:
            classes["C1"] = {"attribution": attribute({a: (v["positive"] if v else None) for a, v in c1.items()}), "detail": c1["u"]}
        # C2 (back-face majority), attributed; void label
        c2 = {a: c2_facing(arms[a]["lights"].get(key)) for a in ("u",) + SINGLES}
        classes["C2"] = {"attribution": attribute(c2), "facing": lu.get("facing"),
                         "label": ("void: lights no surface of either mod" if is_void(lu) else
                                   ("incomplete: " + ", ".join(lu["incomplete"]) if c2["u"] is None and lu.get("incomplete") else None))}
        # C3a: fixtures within H of the light in u
        for d, fx in _fixtures_near(u, lu["pos"], p.host_radius):
            vals = {a: c3a_doubled(fx, arms[a], mods, p)["positive"] if fx in arms[a]["refs"] else None for a in ("u",) + SINGLES}
            if vals["u"]:
                classes["C3a"] = {"attribution": attribute(vals), "fixture": fx}
                break
        printed["C2c"] = c2_leak(lu, [arms[own_name]["lights"].get(key), arms["dep"]["lights"].get(key)], p)
        printed["C4"] = [{"shape": [s["ref"], s["shape"]], "countU": s["count"], **c4_limit((s["ref"], s["shape"]), arms)}
                         for s in u["shapes"].values() if key in s["lights"] and (s["count"] > VANILLA_LIMIT or s.get("strict", 0) > STRICT_LIMIT)]
    # the patched order: an added or moved light that shows C1 / C2 there (the patch's own), or lights a C1b-unlit fixture
    if lp is not None and lp.get("enabled") and action != "DISABLE":
        back_p = c2_facing(lp)
        singles_have = [c2_facing(arms[a]["lights"].get(key)) for a in SINGLES]
        if back_p and not any(singles_have) and (lu is None or not c2_facing(lu)):
            patched["C2"] = {"positiveP": True}
        for d, fx in _fixtures_near(pa, lp["pos"], p.host_radius):
            fo = arms[own_arm(fx, arms, mods)]
            if fx in u["refs"] and c1b_unlit(fx, fo, u, p)["positive"]:
                patched["C1b"] = {"fixedInP": True, "fixture": fx}
                break
        if lu is not None and classes.get("C2", {}).get("attribution") == "needs both" and back_p:
            patched["C2still"] = {"positiveP": True}
    # room doubling: other-mod lights whose spheres meet this light's sphere in u
    doubling = None
    if lu is not None and lu.get("enabled"):
        mine = mod_of(key, u, mods)
        others = [l for l in u["lights"].values() if l.get("enabled") and l["key"] != key and mod_of(l["key"], u, mods) not in (None, mine)
                  and math.dist(l["pos"], lu["pos"]) < l["radius"] + lu["radius"]]
        if others and mine is not None:
            doubling = {"lights": len(others) + 1}
    v = verdict(action, classes, patched, doubling if "DISABLE" in action else None)
    return {"key": key, "light": True, "own": own_name, "classes": classes, "patched": patched, "printed": printed, **v}


def fold(arm_paths, mods, edits, p: Params):
    arms = {a: load_arm(path) for a, path in arm_paths.items()}
    out = []
    for e in edits:
        recs = [judge_record(k, e["action"], arms, mods, p) for k in e["records"]]
        out.append({"id": e["id"], "action": e["action"], "records": recs})
    return out


def main(argv):
    ap = argparse.ArgumentParser(description="Light-clash fold over lightprobe outputs.")
    f = ap
    f.add_argument("--arm", action="append", required=True, help="NAME=lightprobe.json for u, plusa, plusb, dep and p")
    f.add_argument("--mods", nargs=2, required=True, help="the two mods' plugin file names (plusa's, then plusb's)")
    f.add_argument("--edits", required=True, help="JSON list of {id, action, records}")
    f.add_argument("--out", required=True)
    f.add_argument("--host-radius", type=float, default=64.0)
    f.add_argument("--leak-threshold", type=float, default=607.6)
    a = ap.parse_args(argv)
    with Run("lightclash", stage_source="single-stage"):
        arms = dict(x.split("=", 1) for x in a.arm)
        missing = {"u", "plusa", "plusb", "dep", "p"} - set(arms)
        if missing:
            print("missing arms:", sorted(missing), file=sys.stderr)
            return 2
        res = fold(arms, tuple(a.mods), json.load(open(a.edits, encoding="utf-8")), Params(a.host_radius, a.leak_threshold))
        json.dump({"policy": POLICY, "hostRadius": a.host_radius, "edits": res}, open(a.out, "w", encoding="utf-8"), indent=1)
        print(f"{len(res)} edits -> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
