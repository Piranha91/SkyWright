"""Light-clash fold over lightprobe outputs (lane L, 2026-10-04).

Given lightprobe JSON for the combined order (u), each mod alone (plusa, plusb), the dependencies alone (dep) and the patched
order (p), decides for each patch-edited light whether the combination of the two mods produces a lighting problem that
neither produces alone, and what the patch edit does about it. Classes (engine basis in the lane's Gate A):
  C1   orphaned light: in its own mod's arrangement the light has a host (below); in the combination that host is gone or its
       emitting parts are beyond H of the light. C1b, the mirror: a fixture its own arrangement lights has no enabled light within
       H of its emitting parts in the combination. A free-standing light (no host) is not an orphan: C1 does not apply.
  Host (the structural fixture rule, 2026-10-13; no name or editor-ID test): a LIGH whose base has a model hosts itself;
       otherwise the enabled reference whose nearest emitting part (lightprobe `emitters`: an emissive lighting-shader shape with
       value >= the emit threshold, a self-lit effect shape, a self-lit particle emitter's extent) is within H of the light, nearest
       first; inventory items (INVENTORY_ITEM_TYPES) never host; an unreadable model whose bounds reach within H leaves the host
       unknown (not measured, never free-standing). Hosts are found in the light's own single-mod arrangement; co-movement against
       the dependency-only layout is reported (coMoved / coMovedNotHost), never makes a host. A fixture is a reference with
       emitting parts.
  C2   wrong side of the surfaces: most first hits from the light (64 rays inside its radius) are BACK faces, attributed by
       the four-setup rule, and only when at least 8 of the 64 rays hit (fewer: cannot tell, few hits). "void": no surface
       of either mod within its radius (it lights nothing; decided by the leak sampler, not the rays). The leak magnitude
       (C2c) is printed, never decides: the calibration found 74.8 % of vanilla non-shadow lights above its frozen threshold
       (clutter between a light and its walls counts as leak).
  C3a  doubled fixture: one fixture's emitting parts within H of enabled lights from both mods.
  C4   per-shape limit (vanilla renderer: > 7 lights or > 4 shadow lights on one drawn shape; Light Limit Fix reading: > 7
       portal-strict lights): COUNT ONLY, never a clash by itself (whether the engine shows it is an in-game question). A
       shape over the limit only with both mods is an undecided check: such an edit reads cannot tell, never taste. The
       deciding reading is Light Limit Fix's when the evaluated instance has it installed (lightprobe identity.llf), else the
       vanilla one; both are printed. A light counts toward a shape only if its radius reaches the shape's placed box.
Attribution (four-setup): positive in u and in no single-mod or dependency-only arrangement where the light is present.
Verdicts per edit: fixes an inter-mod clash / patch light clash / taste (no clash) / cannot tell (with the class and reason).
A DISABLE that reads taste while both mods' lights reach each other says so ("both mods' lights enabled together: N lights,
brightness not judged") and lists the light's nearest fixture, its distance against H = 48 / 64 / 96 and
whether the combination moved or removed it (a fixture left behind is C1, a clash). Arms from a lightprobe without the emitter
reading (policy lacking "emitters v1") are refused.

usage: python lightclash.py --arm u=U.json --arm plusa=A.json --arm plusb=B.json --arm dep=D.json --arm p=P.json
                                 --mods A.esp B.esp --edits EDITS.json --out OUT.json [--host-radius 64] [--emit-threshold 0.02]
EDITS.json: [{"id": ..., "action": "DISABLE"|"ADD"|"MOVE"|..., "records": ["XXXXXX:Plugin.esp", ...]}, ...]
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

POLICY = "light_clash_fold_v2"
EMITTERS_POLICY = "emitters v1"   # the lightprobe policy fragment the fold needs: arms without it are refused
#: Inventory items never host a light: they are carried, and a carryable light source is a LIGH with its own model (rule step 1). A record
#: type list, not a name test; Flora, Activator, Furniture, Static, MoveableStatic and Light are never in it (review of Gate A, finding 2).
INVENTORY_ITEM_TYPES = frozenset({"Ingredient", "Ingestible", "SoulGem", "MiscItem", "Book", "Weapon", "Armor", "Ammunition", "Key", "Scroll"})
EMIT_THRESHOLD = 0.02   # smallest max(emissive RGB) x multiple that emits (about 5 / 255); LightReach.EmitThreshold
CO_MOVE_TOL = 1.0       # u: two position deltas within this are the same delta; a delta at most this is no move
SINGLES = ("plusa", "plusb", "dep")
VANILLA_LIMIT, VANILLA_SHADOW_LIMIT, STRICT_LIMIT = 7, 4, 7
MIN_FACING_HITS = 8   # the back-face test decides only when at least this many of the 64 rays hit a surface
HS_REPORTED = (48.0, 64.0, 96.0)   # the H values the room-doubling fixture listing reports against


@dataclass(frozen=True)
class Params:
    host_radius: float
    leak_threshold: float
    emit_threshold: float = EMIT_THRESHOLD


def _on(r):
    return bool(r.get("enabled")) and r.get("resolved", True) and not r.get("deleted", False)


# ---- the structural fixture rule (lane A, 2026-10-13; runs/2026-10-13-light-fixtures/GATE-A.md section 2). A fixture is a placed object
# whose mesh has emitting parts (lightprobe's `emitters`: emissive lighting-shader shapes, self-lit effect shapes, self-lit particle emitter
# extents); a light is hosted by the object whose emitting part is within H of it, by its own model when the LIGH has one, and by nothing
# otherwise. No name or editor-ID test.

def _box_distance(pos, box):
    """Distance from a point to a placed box [corner, edgeX, edgeY, edgeZ] (0 inside), measured in the box's own frame
    (LightReach.MeetsShapeBox's measure)."""
    o, edges = box[0], box[1:]
    d = [pos[i] - o[i] for i in range(3)]
    sq, units = 0.0, []
    for e in edges:
        ln = math.sqrt(sum(c * c for c in e))
        if ln <= 0:
            continue
        u = [c / ln for c in e]
        units.append(u)
        t = sum(d[i] * u[i] for i in range(3))
        off = -t if t < 0 else (t - ln if t > ln else 0.0)
        sq += off * off
    if len(units) < 3:   # a flat box: the offset out of its span counts in full
        rest = list(d)
        for u in units:
            k = sum(d[i] * u[i] for i in range(3))
            rest = [rest[i] - k * u[i] for i in range(3)]
        sq += sum(c * c for c in rest)
    return math.sqrt(sq)


def _parts(r, emit=EMIT_THRESHOLD):
    """The reference's emitting parts that pass the threshold (emissive parts by value; self-lit effects and particle emitters always)."""
    return [e for e in r.get("emitters") or [] if e["kind"] != "emissive" or (e.get("value") or 0) >= emit]


def part_distance(pos, r, emit=EMIT_THRESHOLD):
    """Distance from a point to the reference's nearest emitting part (None when it has none)."""
    n = _nearest_part(pos, r, emit)
    return None if n is None else n[0]


def _nearest_part(pos, r, emit=EMIT_THRESHOLD):
    best = None
    for e in _parts(r, emit):
        d = _box_distance(pos, e["box"])
        if best is None or d < best[0]:
            best = (d, e)
    return best


def is_emitting(r, emit=EMIT_THRESHOLD):
    """A fixture: an enabled reference, not an inventory item and not a light, with an emitting part."""
    return _on(r) and r.get("type") not in INVENTORY_ITEM_TYPES and r.get("type") != "Light" and bool(_parts(r, emit))


def emitting_near(arr, pos, h, emit=EMIT_THRESHOLD):
    """Fixtures whose nearest emitting part is within h of pos, as sorted (distance, key)."""
    out = []
    for r in arr["refs"].values():
        if is_emitting(r, emit):
            d = part_distance(pos, r, emit)
            if d is not None and d <= h:
                out.append((d, r["key"]))
    return sorted(out)


def _unknown_near(arr, pos, h, skip):
    """Enabled non-item references whose emitter reading was refused and whose record bounds reach within h of pos."""
    out = []
    for r in arr["refs"].values():
        if r["key"] == skip or not _on(r) or not r.get("emitterStatus") or r.get("type") in INVENTORY_ITEM_TYPES or r.get("type") == "Light":
            continue
        ob = r.get("obnd") or [0, 0, 0]
        reach = math.sqrt(sum(c * c for c in ob)) / 2 * (r.get("scale") or 1.0)
        if math.dist(pos, r["pos"]) <= h + reach:
            out.append(r["key"])
    return sorted(out)


def host_of(light_key, arr, h, emit=EMIT_THRESHOLD):
    """Which placed object hosts this light in this arrangement: {host, how, dist, label, part, effectOnly}."""
    lref = arr["refs"].get(light_key)
    if lref is not None and lref.get("mesh"):
        return {"host": light_key, "how": "own model", "dist": 0.0, "label": "own model", "part": None, "effectOnly": False}
    l = arr["lights"].get(light_key) or lref
    best = None
    for r in arr["refs"].values():
        if r["key"] == light_key or not is_emitting(r, emit):
            continue
        np_ = _nearest_part(l["pos"], r, emit)
        if np_ is not None and np_[0] <= h and (best is None or (np_[0], r["key"]) < (best[0], best[1])):
            best = (np_[0], r["key"], np_[1], r)
    if best is not None:
        d, k, part, r = best
        return {"host": k, "how": part["kind"], "dist": d, "label": None, "part": part.get("part"),
                "effectOnly": all(e["kind"] != "emissive" for e in _parts(r, emit))}
    unknown = _unknown_near(arr, l["pos"], h, light_key)
    if unknown:
        return {"host": None, "how": None, "dist": None, "label": "host unknown (model unreadable): " + ", ".join(unknown), "unknown": unknown}
    return {"host": None, "how": None, "dist": None, "label": "free-standing (no emitting part within H)"}


def _delta(a, b):
    return [a[i] - b[i] for i in range(3)]


def co_movement(light_key, host_key, own, dep, h):
    """Co-movement between the dependency-only layout and the light's own arrangement (support only, never a host)."""
    if dep is None or own is dep:
        return None, []
    l_own, l_dep = own["lights"].get(light_key), dep["lights"].get(light_key)
    if l_own is None or l_dep is None:
        return None, []
    dl = _delta(l_own["pos"], l_dep["pos"])
    if math.sqrt(sum(c * c for c in dl)) <= CO_MOVE_TOL:
        return None, []

    def same(k):
        a, b = own["refs"].get(k), dep["refs"].get(k)
        if a is None or b is None:
            return False
        dr = _delta(a["pos"], b["pos"])
        return math.sqrt(sum(c * c for c in dr)) > CO_MOVE_TOL and math.dist(dr, dl) <= CO_MOVE_TOL
    co = None if host_key is None or host_key == light_key else same(host_key)
    others = sorted(k for k in own["refs"] if k not in (light_key, host_key) and own["refs"][k].get("type") != "Light" and same(k))
    return co, others


def c1_orphan(light_key, own, u, p: Params, dep=None):
    l_own = own["lights"].get(light_key)
    if l_own is None or not l_own.get("enabled"):
        return {"applies": False, "positive": False, "label": "not lit in its own arrangement"}
    hs = host_of(light_key, own, p.host_radius, p.emit_threshold)
    if hs["how"] == "own model":
        return {"applies": False, "positive": False, "host": light_key, "hostKind": "own model", "label": "hosted by its own model"}
    co, others = co_movement(light_key, hs["host"], own, dep, p.host_radius)
    extra = {"coMovedNotHost": others} if others else {}
    if hs["host"] is None:
        if hs["label"].startswith("host unknown"):
            return {"applies": True, "positive": None, "label": hs["label"], **extra}
        return {"applies": False, "positive": False, "label": hs["label"], **extra}
    host = hs["host"]
    base = {"applies": True, "host": host, "hostKind": hs["how"], "dOwn": hs["dist"], **extra}
    if co is not None:
        base["coMoved"] = co
    l_u = u["lights"].get(light_key)
    if l_u is None or not l_u.get("enabled"):
        return {**base, "positive": False, "label": "not lit in the combination"}
    h = u["refs"].get(host)
    if h is not None and _on(h):
        d = part_distance(l_u["pos"], h, p.emit_threshold)
        if d is None:
            why = h.get("emitterStatus") or "no emitting part in the combination"
            return {**base, "positive": None, "label": "host present, emitting parts not read in the combination: " + why}
        if d <= p.host_radius:
            return {**base, "positive": False, "distU": d}
        return {**base, "positive": True, "distU": d, "label": f"host {d:.0f} u away in the combination"}
    return {**base, "positive": True, "distU": None, "label": "host gone in the combination"}


def c1b_unlit(fixture_key, own, u, p: Params):
    f_own = own["refs"].get(fixture_key)
    if f_own is None or not is_emitting(f_own, p.emit_threshold):
        return {"applies": False, "positive": False}
    lit_own = [l for l in own["lights"].values() if l.get("enabled") and part_distance(l["pos"], f_own, p.emit_threshold) <= p.host_radius]
    if not lit_own:
        return {"applies": False, "positive": False, "label": "not lit in its own arrangement"}
    f_u = u["refs"].get(fixture_key)
    if f_u is None or not _on(f_u):
        return {"applies": False, "positive": False, "label": "fixture absent in the combination"}
    if part_distance(f_u["pos"], f_u, p.emit_threshold) is None:
        return {"applies": True, "positive": None, "label": "fixture's emitting parts not read in the combination"}
    lit_u = [l["key"] for l in u["lights"].values() if l.get("enabled") and part_distance(l["pos"], f_u, p.emit_threshold) <= p.host_radius]
    return {"applies": True, "positive": not lit_u, "litBy": lit_u}


def mod_of(key, arr, mods):
    plugin = key.split(":", 1)[1] if ":" in key else ""
    if plugin in mods:
        return plugin
    r = arr["refs"].get(key) or {}
    return r.get("responsible") if r.get("responsible") in mods else None


def c3a_doubled(fixture_key, arr, mods, p: Params):
    f = arr["refs"].get(fixture_key)
    if f is None or not is_emitting(f, p.emit_threshold):
        return {"applies": False, "positive": None}
    lights = [l["key"] for l in arr["lights"].values() if l.get("enabled") and part_distance(l["pos"], f, p.emit_threshold) <= p.host_radius]
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
    if f["front"] + f["back"] < MIN_FACING_HITS:
        return False if is_void(light) else None   # void: lights nothing (decided); otherwise too few hits to decide
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


def verdict(action, classes_u, patched, doubling, c4=(), c4_decider="vanilla"):
    made = [c for c, v in patched.items() if v.get("positiveP")]
    if made:
        return {"verdict": "patch light clash", "text": "the patched order shows " + ", ".join(sorted(made)), "roomDoubling": False}
    fixed = [c for c, v in classes_u.items() if v.get("attribution") == "needs both" and c in DECIDING]
    fixed += [c for c, v in patched.items() if v.get("fixedInP")]
    if fixed:
        return {"verdict": "fixes an inter-mod clash", "text": "the combination alone shows " + ", ".join(sorted(set(fixed))) + "; the edit removes it",
                "roomDoubling": False}
    open_ = [(c, v) for c, v in classes_u.items() if c in DECIDING and v.get("attribution") == "not measured in u" and v.get("applies", True)]
    # C4 is count-only (its engine behaviour is unsettled): a shape over the limit only with both mods, under either reading, is a
    # check that has not decided, so the edit cannot read taste
    unsettled = [r for r in c4 if r.get(c4_decider, {}).get("attribution") == "needs both"]
    if open_ or unsettled:
        parts = [f"{c} not measured in u ({v.get('label', 'no reading')})" for c, v in open_]
        if unsettled:
            parts.append(f"C4: over the light limit only with both mods ({len(unsettled)} shape(s), {c4_decider} reading decides); "
                         "engine behaviour unsettled")
        out = {"verdict": "cannot tell", "text": "; ".join(parts), "roomDoubling": False}
        if unsettled:
            out["nextAction"] = "lab station: per-shape light limit on " + ", ".join(f"{r['shape'][0]} shape {r['shape'][1]}" for r in unsettled[:3])
            out["unsettled"] = ["C4"]
        return out
    text = "every applicable class read no clash only the combination makes"
    if doubling:
        return {"verdict": "taste (no clash)", "roomDoubling": True,
                "text": f"both mods' lights enabled together: {doubling['lights']} lights, brightness not judged; " + text}
    return {"verdict": "taste (no clash)", "text": text, "roomDoubling": False}


# ---------------------------------------------------------------- the fold over lightprobe outputs

def c4_decider(arms):
    """The C4 reading that decides: Light Limit Fix's when the evaluated instance has it installed (lightprobe's identity.llf,
    read through the order's virtual file system), else the vanilla renderer's. Both readings are always printed."""
    return "llf" if (arms.get("u", {}).get("llf") or {}).get("installed") else "vanilla"


def load_arm(path):
    d = json.load(open(path, encoding="utf-8"))
    pol = (d.get("identity") or {}).get("policy") or ""
    if EMITTERS_POLICY not in pol:
        raise ValueError(f"{path} was written by a lightprobe without the emitter reading (policy '{pol}'); "
                         "re-run lightprobe with --geometry from a build that writes emitters.")
    arr = {"refs": {}, "lights": {}, "shapes": {}, "cellOf": {}, "llf": (d.get("identity") or {}).get("llf")}
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
        c1 = {a: (c1_orphan(key, own, arms[a], p, dep=arms.get("dep")) if key in arms[a]["lights"] else None) for a in ("u",) + SINGLES}
        if c1["u"]["applies"]:
            classes["C1"] = {"attribution": attribute({a: (v["positive"] if v else None) for a, v in c1.items()}), "detail": c1["u"]}
        # C2 (back-face majority), attributed; void label
        c2 = {a: c2_facing(arms[a]["lights"].get(key)) for a in ("u",) + SINGLES}
        hits_u = lu["facing"]["front"] + lu["facing"]["back"] if "facing" in lu else None
        classes["C2"] = {"attribution": attribute(c2), "facing": lu.get("facing"),
                         "label": ("void: lights no surface of either mod" if is_void(lu) else
                                   (f"few hits ({hits_u} of {lu['facing']['rays']} rays hit; {MIN_FACING_HITS} needed)" if hits_u is not None and hits_u < MIN_FACING_HITS else
                                    ("incomplete: " + ", ".join(lu["incomplete"]) if c2["u"] is None and lu.get("incomplete") else None)))}
        # C3a: fixtures within H of the light in u
        for d, fx in emitting_near(u, lu["pos"], p.host_radius, p.emit_threshold):
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
        for d, fx in emitting_near(pa, lp["pos"], p.host_radius, p.emit_threshold):
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
    decider = c4_decider(arms)
    v = verdict(action, classes, patched, doubling if "DISABLE" in action else None, printed.get("C4") or (), decider)
    out = {"key": key, "light": True, "own": own_name, "classes": classes, "patched": patched, "printed": printed, "c4Decider": decider, **v}
    if v.get("roomDoubling"):
        out["doublingFixture"] = doubling_fixture(key, own, u, p)
    return out


def doubling_fixture(key, own, u, p: Params):
    """For a room-doubling edit: the light's nearest fixture in its own arrangement, its distance against each reported H, and
    whether the combination moved or removed it. leftBehind (at the fold's H) is exactly C1's positive case: a fixture within H
    of the light in its own arrangement that, in the combination, is gone or no longer within H of the light."""
    l_own = own["lights"].get(key)
    if l_own is None:
        return {"key": None, "label": "light absent from its own arrangement"}
    near = sorted((part_distance(l_own["pos"], r, p.emit_threshold), r["key"]) for r in own["refs"].values() if is_emitting(r, p.emit_threshold))
    if not near:
        return {"key": None, "label": "no fixture in its own arrangement"}
    d, fk = near[0]
    f_u, l_u = u["refs"].get(fk), u["lights"].get(key)
    gone = f_u is None or not _on(f_u)
    moved = (not gone) and math.dist(f_u["pos"], own["refs"][fk]["pos"]) > 1.0
    d_u = None if gone or l_u is None else part_distance(l_u["pos"], f_u, p.emit_threshold)
    def left(h):
        return d <= h and (gone or d_u is None or d_u > h)
    fo = own["refs"][fk]
    return {"key": fk, "edid": fo.get("edid"), "type": fo.get("type"), "mesh": fo.get("mesh"),
            "kinds": sorted({e["kind"] for e in _parts(fo, p.emit_threshold)}), "dOwn": d, "dU": d_u,
            "withinH": {f"{h:g}": d <= h for h in HS_REPORTED}, "movedInU": moved, "goneInU": gone,
            "leftBehind": left(p.host_radius), "leftBehindByH": {f"{h:g}": left(h) for h in HS_REPORTED}}


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
    f.add_argument("--emit-threshold", type=float, default=EMIT_THRESHOLD, help="smallest emissive value x multiple that emits")
    f.add_argument("--leak-threshold", type=float, required=True, help="T_leak from the calibration (C2c prints only)")
    a = ap.parse_args(argv)
    with Run("lightclash", stage_source="single-stage"):
        arms = dict(x.split("=", 1) for x in a.arm)
        missing = {"u", "plusa", "plusb", "dep", "p"} - set(arms)
        if missing:
            print("missing arms:", sorted(missing), file=sys.stderr)
            return 2
        res = fold(arms, tuple(a.mods), json.load(open(a.edits, encoding="utf-8")), Params(a.host_radius, a.leak_threshold, a.emit_threshold))
        json.dump({"policy": POLICY, "hostRadius": a.host_radius, "emitThreshold": a.emit_threshold, "edits": res}, open(a.out, "w", encoding="utf-8"), indent=1)
        print(f"{len(res)} edits -> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
