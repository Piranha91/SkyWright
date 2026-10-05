"""Known answers for lightclash.py (the light-clash fold over lightprobe outputs). Each test names the wrong answer it
rejects. Run: python -m pytest _geometry-harness/tests/test_lightclash.py -q   (or python this file)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import lightclash as lc  # noqa: E402

A, B, BASE = "ModA.esp", "ModB.esp", "Skyrim.esm"
MODS = (A, B)
P = lc.Params(host_radius=64.0, leak_threshold=10.0)


def ref(key, edid, pos, enabled=True, typ="Static", mesh=None, responsible=None):
    return {"key": key, "edid": edid, "type": typ, "pos": list(pos), "enabled": enabled, "resolved": True, "deleted": False,
            "mesh": mesh, "responsible": responsible or key.split(":")[1]}


def light(key, pos, enabled=True, radius=280, shadow=False, strict=True, leak=None, lit=0.0, facing=None, incomplete=None, edid="SomeLight"):
    row = {"key": key, "edid": edid, "pos": list(pos), "enabled": enabled, "radius": radius, "shadow": shadow, "strict": strict}
    if enabled and leak is not None:
        row["leak"] = {"lit": lit, "leak": leak, "leakByRef": {}, "litByRef": {}}
        row["facing"] = facing or {"rays": 64, "front": 64, "back": 0, "none": 0}
        row["pointInSolid"] = []
    if incomplete:
        row["incomplete"] = incomplete
    return row


def arr(refs, lights=(), shapes=()):
    a = {"refs": {r["key"]: r for r in refs}, "lights": {l["key"]: l for l in lights}, "shapes": {}}
    for s in shapes:
        a["shapes"][(s["ref"], s["shape"])] = s
    for l in lights:   # a light is also a reference
        a["refs"].setdefault(l["key"], ref(l["key"], l["edid"], l["pos"], l["enabled"], typ="Light"))
    return a


CANDLE = "000100:Skyrim.esm"
LB = "000900:ModB.esp"


def test_fixture_patterns_match_and_skip():
    """rejects: every Static read as a fixture; a candle not read as one"""
    assert lc.is_fixture({"edid": "CandleHornTable01", "mesh": "clutter/candles/candlehorntable01.nif"})
    assert lc.is_fixture({"edid": "FireplaceWood01Burning", "mesh": None})
    assert not lc.is_fixture({"edid": "Barrel02Static", "mesh": "clutter/barrels/barrel02.nif"})


def test_c1_orphan_when_other_mod_moves_the_host():
    """rejects: no clash when the fixture a light was placed for is moved 500 u away by the other mod"""
    own = arr([ref(CANDLE, "CandleHornTable01", (40, 0, 0))], [light(LB, (0, 0, 0))])
    u = arr([ref(CANDLE, "CandleHornTable01", (500, 0, 0), responsible=A)], [light(LB, (0, 0, 0))])
    r = lc.c1_orphan(LB, own, u, P)
    assert r["applies"] and r["positive"] and r["host"] == CANDLE


def test_c1_fill_light_not_an_orphan():
    """rejects: every light without a fixture nearby is an orphan"""
    own = arr([ref(CANDLE, "CandleHornTable01", (400, 0, 0))], [light(LB, (0, 0, 0))])
    u = arr([ref(CANDLE, "CandleHornTable01", (400, 0, 0))], [light(LB, (0, 0, 0))])
    r = lc.c1_orphan(LB, own, u, P)
    assert r["applies"] is False and r["positive"] is False


def test_c1b_unlit_fixture():
    """rejects: a fixture whose only light the other mod disabled read as still lit"""
    LA = "000800:ModA.esp"
    own = arr([ref("000200:ModA.esp", "CandleHornTable01", (0, 0, 0))], [light(LA, (10, 0, 0))])
    u = arr([ref("000200:ModA.esp", "CandleHornTable01", (0, 0, 0))], [light(LA, (10, 0, 0), enabled=False)])
    r = lc.c1b_unlit("000200:ModA.esp", own, u, P)
    assert r["applies"] and r["positive"]


def test_c3a_doubled_needs_two_mods():
    """rejects: two lights of the SAME mod on one candle counted as doubled"""
    u2 = arr([ref(CANDLE, "CandleHornTable01", (0, 0, 0))], [light("000801:ModA.esp", (10, 0, 0)), light(LB, (0, 10, 0))])
    assert lc.c3a_doubled(CANDLE, u2, MODS, P)["positive"] is True
    same = arr([ref(CANDLE, "CandleHornTable01", (0, 0, 0))], [light("000901:ModB.esp", (10, 0, 0)), light(LB, (0, 10, 0))])
    assert lc.c3a_doubled(CANDLE, same, MODS, P)["positive"] is False


def test_c2_leak_positive_only_above_own_arrangement():
    """rejects: a leak present in the light's own arrangement counted as the pair's"""
    assert lc.c2_leak(light(LB, (0, 0, 0), leak=500.0), [light(LB, (0, 0, 0), leak=0.0)], P)["positive"] is True
    assert lc.c2_leak(light(LB, (0, 0, 0), leak=500.0), [light(LB, (0, 0, 0), leak=495.0)], P)["positive"] is False


def test_c2_shadow_light_not_applicable():
    """rejects: leak counted for a shadow-casting light (its shadow map occludes it)"""
    r = lc.c2_leak(light(LB, (0, 0, 0), leak=500.0, shadow=True), [light(LB, (0, 0, 0), leak=0.0, shadow=True)], P)
    assert r["applies"] is False and r["positive"] is False and "shadow" in r["label"]


def test_c2_incomplete_negative_is_not_a_negative():
    """rejects: a negative leak reading with an unreadable model in range reported as no clash"""
    r = lc.c2_leak(light(LB, (0, 0, 0), leak=0.0, incomplete=["000555:ModA.esp"]), [light(LB, (0, 0, 0), leak=0.0)], P)
    assert r["positive"] is None and "incomplete" in r["label"]


def shape(sref, n, lights, strict=None, shadow=0):
    return {"ref": sref, "shape": n, "count": len(lights), "lights": lights, "strict": len(lights) if strict is None else strict, "shadow": shadow}


def test_c4_over_limit_only_in_combination():
    """rejects: any count over 7 is a clash (one mod alone already over the limit is not the pair)"""
    la = [f"00080{i}:ModA.esp" for i in range(4)]
    lb = [f"00090{i}:ModB.esp" for i in range(4)]
    s = "000300:Skyrim.esm"
    arms = {"u": arr([], [], [shape(s, 0, la + lb)]), "plusa": arr([], [], [shape(s, 0, la)]),
            "plusb": arr([], [], [shape(s, 0, lb)]), "dep": arr([], [], [])}
    r = lc.c4_limit((s, 0), arms)
    assert r["vanilla"]["attribution"] == "needs both"
    arms["plusb"] = arr([], [], [shape(s, 0, lb + [f"00091{i}:ModB.esp" for i in range(4)])])
    assert lc.c4_limit((s, 0), arms)["vanilla"]["attribution"] == "not the pair"


def test_c4_llf_reading_counts_strict_only():
    """rejects: the Light Limit Fix reading counting non-strict lights (they are clustered there)"""
    s = "000300:Skyrim.esm"
    ls = [f"00080{i}:ModA.esp" for i in range(8)]
    arms = {"u": arr([], [], [shape(s, 0, ls, strict=3)]), "plusa": arr([], [], [shape(s, 0, ls[:4], strict=3)]),
            "plusb": arr([], [], [shape(s, 0, ls[4:], strict=0)]), "dep": arr([], [], [])}
    r = lc.c4_limit((s, 0), arms)
    assert r["vanilla"]["positiveU"] is True and r["llf"]["positiveU"] is False and r["disagree"] is True


def test_attribution_four_setup():
    """rejects: positive in u alone called the pair's when a single-mod arrangement already shows it"""
    assert lc.attribute({"u": True, "plusa": False, "plusb": False, "dep": None}) == "needs both"
    assert lc.attribute({"u": True, "plusa": False, "plusb": True, "dep": None}) == "not the pair"
    assert lc.attribute({"u": False, "plusa": True, "plusb": None, "dep": None}) == "no clash in u"
    assert lc.attribute({"u": None, "plusa": False, "plusb": False, "dep": None}) == "not measured in u"


def test_d3_light_kind():
    """rejects: a Light base judged by the standing-object floor rule (D3)"""
    assert lc.placement_kind("Light", "SomeFillLightNS") == "light"
    assert lc.placement_kind("Static", "Barrel02Static") == "stand"
    assert lc.floor_rule_applies("light") is False


def test_verdict_disable_of_positive_light_fixes():
    """rejects: a DISABLE that removes a light clashing only in the combination read as taste"""
    v = lc.verdict("DISABLE", {"C2": {"attribution": "needs both"}}, patched={}, doubling=None)
    assert v["verdict"] == "fixes an inter-mod clash" and "C2" in v["text"]


def test_verdict_room_doubling_taste_is_labelled():
    """rejects: a room-doubling DISABLE read as plain taste with nothing said (review F1)"""
    v = lc.verdict("DISABLE", {"C1": {"attribution": "no clash in u"}, "C2": {"attribution": "no clash in u"}, "C3a": {"attribution": "no clash in u"},
                               "C4": {"attribution": "no clash in u"}}, patched={}, doubling={"lights": 12})
    assert v["verdict"] == "taste (no clash)" and v["roomDoubling"] is True
    assert "both mods' lights enabled together: 12 lights, brightness not judged" in v["text"]


def test_verdict_refused_class_is_cannot_tell_with_reason():
    """rejects: 'taste' when a class that applies could not be measured"""
    v = lc.verdict("DISABLE", {"C1": {"attribution": "no clash in u"}, "C2": {"attribution": "not measured in u", "label": "incomplete: 000555"}},
                   patched={}, doubling=None)
    assert v["verdict"] == "cannot tell" and "C2" in v["text"] and "incomplete" in v["text"]


def test_verdict_add_lighting_an_unlit_fixture_fixes():
    """rejects: every ADD read as taste; an ADD that restores light to a fixture the combination left unlit"""
    v = lc.verdict("ADD", {}, patched={"C1b": {"fixedInP": True}}, doubling=None)
    assert v["verdict"] == "fixes an inter-mod clash" and "C1b" in v["text"]


def test_verdict_add_that_leaks_in_p_is_a_patch_clash():
    """rejects: a patch-added light that leaks through a wall in the patched order read as taste"""
    v = lc.verdict("ADD", {}, patched={"C2": {"positiveP": True}}, doubling=None)
    assert v["verdict"] == "patch light clash" and "C2" in v["text"]


def facing(front, back, none=0):
    return {"rays": front + back + none, "front": front, "back": back, "none": none}


def test_c2_facing_back_majority_and_void():
    """rejects: a light mostly behind surfaces read as inside; an incomplete negative read as a negative; a void light read as lit"""
    assert lc.c2_facing(light(LB, (0, 0, 0), leak=0.0, facing=facing(10, 40, 14))) is True
    assert lc.c2_facing(light(LB, (0, 0, 0), leak=0.0, facing=facing(40, 10, 14))) is False
    assert lc.c2_facing(light(LB, (0, 0, 0), leak=0.0, facing=facing(40, 10, 14), incomplete=["000555:ModA.esp"])) is None
    assert lc.is_void(light(LB, (0, 0, 0), leak=0.0, lit=0.0, facing=facing(0, 0, 64))) is True
    assert lc.is_void(light(LB, (0, 0, 0), leak=0.0, lit=5.0, facing=facing(3, 0, 61))) is False


def arms_for_c2(back_u, back_b):
    lu = light(LB, (0, 0, 0), leak=0.0, lit=1.0, facing=facing(10, 50) if back_u else facing(50, 10))
    lb = light(LB, (0, 0, 0), leak=0.0, lit=1.0, facing=facing(10, 50) if back_b else facing(50, 10))
    lp = light(LB, (0, 0, 0), enabled=False)
    return {"u": arr([], [lu]), "plusb": arr([], [lb]), "plusa": arr([], []), "dep": arr([], []), "p": arr([], [lp])}


def test_judge_record_c2_attributed_disable_fixes():
    """rejects: a light that is behind the other mod's surfaces only in the combination read as taste when the patch disables it"""
    r = lc.judge_record(LB, "DISABLE", arms_for_c2(True, False), MODS, P)
    assert r["classes"]["C2"]["attribution"] == "needs both" and r["verdict"] == "fixes an inter-mod clash"


def test_judge_record_c2_own_mod_already_behind_is_not_the_pair():
    """rejects: a light already behind surfaces in its own mod's arrangement credited to the pair (no-attribution answer)"""
    r = lc.judge_record(LB, "DISABLE", arms_for_c2(True, True), MODS, P)
    assert r["classes"]["C2"]["attribution"] == "not the pair" and r["verdict"] == "taste (no clash)"


C4_NEEDS_BOTH = {"shape": ["000BBA:ModA.esp", 2], "countU": 12, "vanilla": {"positiveU": True, "attribution": "needs both"},
                 "llf": {"positiveU": False, "attribution": "no clash in u"}, "disagree": True}


def test_verdict_count_only_c4_needs_both_is_cannot_tell():
    """rejects: taste while a shape is over the light limit only with both mods - C4 is count-only, so unsettled (Gate C review F1)"""
    v = lc.verdict("DISABLE", {"C2": {"attribution": "no clash in u"}}, patched={}, doubling={"lights": 3}, c4=[C4_NEEDS_BOTH])
    assert v["verdict"] == "cannot tell" and "C4" in v["text"] and "unsettled" in v["text"] and v.get("nextAction")
    assert v["roomDoubling"] is False


def test_verdict_c4_not_the_pair_stays_taste():
    """rejects: every printed C4 row made unsettled - a shape already over the limit in one mod's own arrangement is not the pair"""
    row = dict(C4_NEEDS_BOTH, vanilla={"positiveU": True, "attribution": "not the pair"}, llf={"positiveU": False, "attribution": "no clash in u"})
    v = lc.verdict("DISABLE", {"C2": {"attribution": "no clash in u"}}, patched={}, doubling=None, c4=[row])
    assert v["verdict"] == "taste (no clash)"


def test_verdict_decided_fix_stands_over_unsettled_c4():
    """rejects: a decided C2 fix demoted to cannot tell by an unsettled count"""
    v = lc.verdict("DISABLE", {"C2": {"attribution": "needs both"}}, patched={}, doubling=None, c4=[C4_NEEDS_BOTH])
    assert v["verdict"] == "fixes an inter-mod clash"



# ---- sixth fold (Piranha's 2026-10-05 rulings)

def test_c4_decider_llf_when_installed():
    """rejects: the vanilla reading deciding C4 in an instance with Light Limit Fix installed (rule b)"""
    row = dict(C4_NEEDS_BOTH)   # vanilla needs both, LLF no clash in u
    assert lc.verdict("DISABLE", {"C2": {"attribution": "no clash in u"}}, {}, None, c4=[row], c4_decider="llf")["verdict"] == "taste (no clash)"
    assert lc.verdict("DISABLE", {"C2": {"attribution": "no clash in u"}}, {}, None, c4=[row], c4_decider="vanilla")["verdict"] == "cannot tell"
    assert lc.c4_decider({"u": {"llf": {"installed": True, "how": "SKSE/Plugins/LightLimitFix.dll"}}}) == "llf"
    assert lc.c4_decider({"u": {"llf": {"installed": False}}}) == "vanilla"


def test_c2_few_hits_is_cannot_tell():
    """rejects: the back-face test deciding on fewer than 8 of 64 hitting rays (rule c)"""
    lu = light(LB, (0, 0, 0), leak=0.0, lit=1.0, facing=facing(0, 3, 61))
    lb = light(LB, (0, 0, 0), leak=0.0, lit=1.0, facing=facing(50, 0, 14))
    arms = {"u": arr([], [lu]), "plusb": arr([], [lb]), "plusa": arr([], []), "dep": arr([], []), "p": arr([], [light(LB, (0, 0, 0), enabled=False)])}
    r = lc.judge_record(LB, "DISABLE", arms, MODS, P)
    assert r["classes"]["C2"]["attribution"] == "not measured in u" and "few hits" in r["classes"]["C2"]["label"]
    assert r["verdict"] == "cannot tell"


def test_c2_few_hits_in_p_is_not_a_patch_clash():
    """rejects: a patch-added light with 3 back hits and no front hits in p read as a patch light clash (rule c)"""
    lp = light("000811:Patch.esp", (0, 0, 0), leak=0.0, lit=1.0, facing=facing(0, 3, 61))
    arms = {"u": arr([], []), "plusa": arr([], []), "plusb": arr([], []), "dep": arr([], []), "p": arr([], [lp])}
    r = lc.judge_record("000811:Patch.esp", "ADD", arms, MODS, P)
    assert r["verdict"] != "patch light clash"


def test_room_doubling_lists_the_nearest_fixture():
    """rejects: a room-doubling taste without the light's nearest fixture, its distance against each H, and whether it moved (rule d)"""
    sconce = "000400:Skyrim.esm"
    la = light("000801:ModA.esp", (100, 0, 0), leak=0.0, lit=1.0, facing=facing(50, 0, 14))
    lb = light(LB, (0, 0, 0), leak=0.0, lit=1.0, facing=facing(50, 0, 14))
    fx = ref(sconce, "WallSconce01", (80, 0, 0))
    arms = {"u": arr([fx], [la, lb]), "plusb": arr([fx], [lb]), "plusa": arr([fx], [la]), "dep": arr([fx], []),
            "p": arr([fx], [la, light(LB, (0, 0, 0), enabled=False)])}
    r = lc.judge_record(LB, "DISABLE", arms, MODS, P)
    assert r["roomDoubling"] is True
    f = r["doublingFixture"]
    assert f["key"] == sconce and abs(f["dOwn"] - 80) < 1e-6 and f["withinH"] == {"48": False, "64": False, "96": True}
    assert f["movedInU"] is False and f["goneInU"] is False and f["leftBehind"] is False



if __name__ == "__main__":
    fails = 0
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            try:
                fn(); print("PASS", name)
            except Exception as ex:  # noqa: BLE001
                fails += 1; print("FAIL", name, type(ex).__name__, ex)
    print(f"{fails} failed")
    sys.exit(1 if fails else 0)
