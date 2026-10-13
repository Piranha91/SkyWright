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


def fixture(key, edid, pos, **kw):
    """A fixture as lightprobe now writes one: a reference with an emitting part (a point box at its origin, so every distance the
    tests measured to the origin is unchanged). Fixture correction 2026-10-13: the name rule needed only a name."""
    r = ref(key, edid, pos, **kw)
    r["emitters"] = [{"part": "flame", "kind": "emissive", "value": 0.5, "box": [list(pos), [0, 0, 0], [0, 0, 0], [0, 0, 0]]}]
    return r


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


def test_c1_orphan_when_other_mod_moves_the_host():
    """rejects: no clash when the fixture a light was placed for is moved 500 u away by the other mod"""
    own = arr([fixture(CANDLE, "CandleHornTable01", (40, 0, 0))], [light(LB, (0, 0, 0))])
    u = arr([fixture(CANDLE, "CandleHornTable01", (500, 0, 0), responsible=A)], [light(LB, (0, 0, 0))])
    r = lc.c1_orphan(LB, own, u, P)
    assert r["applies"] and r["positive"] and r["host"] == CANDLE


def test_c1_fill_light_not_an_orphan():
    """rejects: every light without a fixture nearby is an orphan"""
    own = arr([fixture(CANDLE, "CandleHornTable01", (400, 0, 0))], [light(LB, (0, 0, 0))])
    u = arr([fixture(CANDLE, "CandleHornTable01", (400, 0, 0))], [light(LB, (0, 0, 0))])
    r = lc.c1_orphan(LB, own, u, P)
    assert r["applies"] is False and r["positive"] is False


def test_c1b_unlit_fixture():
    """rejects: a fixture whose only light the other mod disabled read as still lit"""
    LA = "000800:ModA.esp"
    own = arr([fixture("000200:ModA.esp", "CandleHornTable01", (0, 0, 0))], [light(LA, (10, 0, 0))])
    u = arr([fixture("000200:ModA.esp", "CandleHornTable01", (0, 0, 0))], [light(LA, (10, 0, 0), enabled=False)])
    r = lc.c1b_unlit("000200:ModA.esp", own, u, P)
    assert r["applies"] and r["positive"]


def test_c3a_doubled_needs_two_mods():
    """rejects: two lights of the SAME mod on one candle counted as doubled"""
    u2 = arr([fixture(CANDLE, "CandleHornTable01", (0, 0, 0))], [light("000801:ModA.esp", (10, 0, 0)), light(LB, (0, 10, 0))])
    assert lc.c3a_doubled(CANDLE, u2, MODS, P)["positive"] is True
    same = arr([fixture(CANDLE, "CandleHornTable01", (0, 0, 0))], [light("000901:ModB.esp", (10, 0, 0)), light(LB, (0, 10, 0))])
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
    fx = fixture(sconce, "WallSconce01", (80, 0, 0))
    arms = {"u": arr([fx], [la, lb]), "plusb": arr([fx], [lb]), "plusa": arr([fx], [la]), "dep": arr([fx], []),
            "p": arr([fx], [la, light(LB, (0, 0, 0), enabled=False)])}
    r = lc.judge_record(LB, "DISABLE", arms, MODS, P)
    assert r["roomDoubling"] is True
    f = r["doublingFixture"]
    assert f["key"] == sconce and abs(f["dOwn"] - 80) < 1e-6 and f["withinH"] == {"48": False, "64": False, "96": True}
    assert f["movedInU"] is False and f["goneInU"] is False and f["leftBehind"] is False



# ---------------------------------------------------------------- the structural fixture rule (lane A, 2026-10-13)
# Known answers K1-K16 (runs/2026-10-13-light-fixtures/GATE-A.md section 3, GATE-B.md). A host is a placed object with an emitting part
# within H of the light, measured to that part's placed box (lightprobe's `emitters`); a LIGH with its own model hosts itself; inventory
# items never host; an unreadable model in range leaves the host unknown. No name or editor-ID test. Each docstring names the wrong
# answer it rejects.

def eref(key, pos, parts=(), typ="Static", edid="Prop01", mesh="props/prop01.nif", status="read", obnd=None, enabled=True):
    """A reference with lightprobe's emitter fields. parts: (kind, centre, half-size or (hx, hy, hz)); each becomes an axis-aligned
    placed box [origin, edgeX, edgeY, edgeZ]."""
    r = ref(key, edid, pos, enabled=enabled, typ=typ, mesh=mesh)
    em = []
    for i, (kind, c, h) in enumerate(parts):
        hx, hy, hz = (h, h, h) if isinstance(h, (int, float)) else h
        part = {"part": f"part{i}", "kind": kind, "box": [[c[0] - hx, c[1] - hy, c[2] - hz], [2 * hx, 0, 0], [0, 2 * hy, 0], [0, 0, 2 * hz]]}
        if kind == "emissive":
            part["value"] = 0.5   # lightprobe writes the emissive value with every emissive part
        em.append(part)
    if em:
        r["emitters"] = em
    if status != "read":
        r["emitterStatus"] = status
    if obnd is not None:
        r["obnd"] = list(obnd)
    return r


T1, C1, W1, X1, F1 = "000A01:Skyrim.esm", "000A02:Skyrim.esm", "000A03:Skyrim.esm", "000A04:Skyrim.esm", "000A05:Skyrim.esm"
LBX = "000B01:ModB.esp"


def _host(refs, lpos, extra_lights=()):
    a = arr(refs, [light(LBX, lpos)] + list(extra_lights))
    return lc.host_of(LBX, a, 64.0)


def test_k1_candle_on_a_table_host_is_the_candle():
    """rejects: no host found because the candle's name matches no pattern (the name rule's fill light); the table chosen by its bounds"""
    table = eref(T1, (0, 0, 0), edid="Prop10", mesh="props/prop10.nif", obnd=(120, 80, 70))
    candle = eref(C1, (0, 0, 70), [("emissive", (0, 0, 80), 2)], edid="Prop11", mesh="props/prop11.nif")
    h = _host([table, candle], (0, 0, 90))
    assert h["host"] == C1, f"K1: host should be the candle (emitting part 8 u away), got {h}"
    assert h["how"] == "emissive", f"K1: host kind should be emissive, got {h}"


def test_k1b_vanilla_named_candle_control():
    """rejects: a regression on the case both rules agree on (control)"""
    table = eref(T1, (0, 0, 0), edid="CommonTableRound01", mesh="clutter/common/commontableround01.nif")
    candle = eref(C1, (0, 0, 70), [("emissive", (0, 0, 80), 2)], edid="CandleHornTable01", mesh="clutter/candles/candlehorntable01.nif")
    assert _host([table, candle], (0, 0, 90))["host"] == C1


def test_k2_sconce_on_a_wall():
    """rejects: the wall shell (no emitter near the light) taken as the host"""
    wall = eref(W1, (0, 0, 0), [("emissive", (-131, 0, 494), (2, 40, 40))], edid="FarmInt2End01", mesh="architecture/farmhouse/interior/farmint2end01.nif")
    sconce = eref(C1, (0, 100, 100), [("emissive", (0, 100, 90), 2)], edid="CandleHornWall01", mesh="clutter/candles/candlehornwall01.nif")
    assert _host([wall, sconce], (0, 100, 70))["host"] == C1


def test_k3_room_shell_without_an_emitter_near_is_free_standing():
    """rejects: a shell enclosing the light read as its host because the light is inside its bounds"""
    shell = eref(W1, (0, 0, 0), [("emissive", (300, 0, 300), 20)], edid="FarmInt2End01", obnd=(800, 600, 600))
    h = _host([shell], (0, 0, 100))
    assert h["host"] is None, f"K3: expected free-standing, got {h}"


def test_k3b_non_emitting_window_is_not_a_host():
    """rejects: a window mesh with no emission taken as a fixture because its name says window"""
    shell = eref(W1, (0, 0, 0), [("emissive", (300, 0, 300), 20)], edid="FarmInt2End01")
    window = eref(X1, (30, 0, 100), edid="Lux_FarmhouseWindow", mesh="architecture/farmhouse/interior/lux_farmhousewindow.nif")
    h = _host([shell, window], (0, 0, 100))
    assert h["host"] is None, f"K3b: a non-emitting window is not a host, got {h}"


def test_k3c_shell_window_glow_part_hosts():
    """rejects: the shell's own glow-map window part ignored because the shell's name matches nothing"""
    shell = eref(W1, (0, 0, 0), [("emissive", (0, 0, 100), (40, 2, 40))], edid="FarmIntLHEnd01")
    h = _host([shell], (0, 5, 100))
    assert h["host"] == W1, f"K3c: the shell hosts by its window part (3 u), got {h}"


def test_k4_light_with_its_own_model_hosts_itself():
    """rejects: a LIGH carrying a model hosted by a nearby candle (the name rule skips Light references)"""
    lt = light(LBX, (0, 0, 0))
    lref = ref(LBX, "Torch01Shadow", (0, 0, 0), typ="Light", mesh="Weapons\\Torch\\torch.nif")
    candle = eref(C1, (20, 0, 0), [("emissive", (20, 0, 5), 2)], edid="CandleHornTable01")
    a = {"refs": {LBX: lref, C1: candle}, "lights": {LBX: lt}, "shapes": {}}
    h = lc.host_of(LBX, a, 64.0)
    assert h["host"] == LBX and h["how"] == "own model", f"K4: the light's own model hosts it, got {h}"
    r = lc.c1_orphan(LBX, a, a, P)
    assert r["applies"] is False and "own model" in (r.get("label") or ""), f"K4: C1 does not apply to a self-hosted light, got {r}"


def test_k5_fire_salts_never_host():
    """rejects: an emitting inventory item (Ingredient) taken as a host; the name rule's 'fire' match"""
    salts = eref(F1, (10, 0, 0), [("emissive", (10, 0, 2), 3)], typ="Ingredient", edid="FireSalts", mesh="clutter/ingredients/firesaltpile.nif")
    h = _host([salts], (0, 0, 0))
    assert h["host"] is None, f"K5: an inventory item never hosts, got {h}"


def test_k5b_resist_fire_never_hosts():
    """rejects: a non-emitting potion taken as a fixture because its editor ID contains 'fire'"""
    potion = eref(F1, (10, 0, 0), typ="Ingestible", edid="ResistFire25", mesh="clutter/potions/potionresistfire50.nif")
    h = _host([potion], (0, 0, 0))
    assert h["host"] is None, f"K5b: got {h}"


def test_k12_non_item_emitters_are_never_excluded():
    """rejects: the item exclusion widened to Flora / Furniture / Activator / MoveableStatic / Static (review finding 2)"""
    for typ, edid in (("Flora", "DeathBell01"), ("Furniture", "EnchantingWorkbench"), ("Activator", "Prop20"),
                      ("MoveableStatic", "Prop21"), ("Static", "Prop22")):
        e = eref(F1, (10, 0, 0), [("emissive", (10, 0, 0), 3)], typ=typ, edid=edid)
        h = _host([e], (0, 0, 0))
        assert h["host"] == F1, f"K12: an emitting {typ} hosts, got {h}"


def test_k6_bounds_and_origin_do_not_count():
    """rejects: hosting by the candle's origin (10 u) when its emitting part is 70 u away"""
    table = eref(T1, (0, 0, 0), edid="CommonTableRound01", obnd=(120, 80, 70))
    candle = eref(C1, (0, 0, 40), [("emissive", (0, 0, 102), 2)], edid="CandleHornTable01")
    h = _host([table, candle], (0, 0, 30))
    assert h["host"] is None, f"K6: emitting part 70 u away at H 64, expected free-standing, got {h}"


def test_k7_particle_only_fire_fx_hosts():
    """rejects: a self-lit particle emitter ignored (no lighting-shader part, neutral name)"""
    fx = eref(X1, (0, 0, 0), [("particle emitter", (0, 0, 10), 4)], typ="MoveableStatic", edid="Prop30", mesh="effects/prop30.nif")
    h = _host([fx], (0, 0, 20))
    assert h["host"] == X1 and h["how"] == "particle emitter", f"K7: got {h}"


def test_k8_lit_effect_fx_is_not_a_host_control():
    """rejects: a cobweb (effect shader lit by scene lights; the reader gives it no emitting part) taken as a host (control)"""
    web = eref(X1, (0, 0, 0), typ="Static", edid="FXcobwebCorner01", mesh="effects/fxcobwebcorner01.nif")
    assert _host([web], (0, 0, 10))["host"] is None


def test_k9_host_found_on_the_own_arm_and_left_behind():
    """rejects: the host looked up in the combination (where it is gone), so the light reads as free-standing"""
    candle = eref(C1, (0, 0, 40), [("emissive", (0, 0, 50), 2)], edid="Prop11")
    own = arr([candle], [light(LBX, (0, 0, 60))])
    u = arr([], [light(LBX, (0, 0, 60))])
    r = lc.c1_orphan(LBX, own, u, P)
    assert r["applies"] and r["positive"] is True and r.get("host") == C1, f"K9: got {r}"


def test_k10_co_movement_supports_but_never_creates():
    """rejects: co-movement ignored; a co-moved object without an emitter made the host"""
    candle0 = eref(C1, (0, 0, 40), [("emissive", (0, 0, 50), 2)], edid="Prop11")
    prop0 = eref(X1, (0, 30, 0), edid="Prop12")
    dep = arr([candle0, prop0], [light(LBX, (0, 0, 60))])
    candle1 = eref(C1, (100, 0, 40), [("emissive", (100, 0, 50), 2)], edid="Prop11")
    prop1 = eref(X1, (100, 30, 0), edid="Prop12")
    own = arr([candle1, prop1], [light(LBX, (100, 0, 60))])
    r = lc.c1_orphan(LBX, own, own, P, dep=dep)
    assert r.get("host") == C1 and r.get("coMoved") is True, f"K10: the host co-moved, got {r}"
    assert X1 in (r.get("coMovedNotHost") or []), f"K10: the co-moved non-emitter is reported, not hosted, got {r}"


def test_k11_unreadable_model_in_range_leaves_the_host_unknown():
    """rejects: an unreadable model next to the light read as 'no emitter', so the light is free-standing and the edit taste"""
    blob = eref(X1, (0, 0, 20), typ="Static", edid="Prop40", status="refused: render_unreadable", obnd=(40, 40, 40))
    h = _host([blob], (0, 0, 40))
    assert h["host"] is None and (h.get("label") or "").startswith("host unknown"), f"K11: got {h}"
    own = arr([blob], [light(LBX, (0, 0, 40))])
    r = lc.c1_orphan(LBX, own, own, P)
    assert r["positive"] is None, f"K11: C1 is not measured when the host is unknown, got {r}"


def test_k13_part_distance_is_to_the_placed_box():
    """rejects: distance measured to the reference's origin rather than its placed emitting part (0 inside, oriented box)"""
    r = eref(C1, (0, 0, 0), [("emissive", (0, 0, 100), 10)])
    d0, d1 = lc.part_distance((0, 0, 100), r), lc.part_distance((0, 0, 130), r)
    assert d0 == 0.0 and abs(d1 - 20.0) < 1e-9, f"K13: inside 0 and 20 u above the box, got {d0}, {d1}"
    rot = {"key": C1, "pos": [0, 0, 0], "emitters": [{"part": "p", "kind": "emissive", "value": 0.5,
           "box": [[0, 0, 0], [70.71068, 70.71068, 0], [-70.71068, 70.71068, 0], [0, 0, 100]]}]}
    d2 = lc.part_distance((35.35534, -35.35534, 50), rot)
    assert abs(d2 - 50.0) < 1e-3, f"K13: a 45-degree box measured in its own frame is 50 u away, got {d2}"


def test_k14_arm_without_emitters_is_refused():
    """rejects: an arm from a lightprobe without the emitter reading folded as if no light had a host"""
    import json, tempfile, os
    d = {"identity": {"policy": "light_reach_v4|winding-normal front"}, "cells": []}
    fd, path = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    with open(path, "w") as fh:
        json.dump(d, fh)
    try:
        refused = False
        try:
            lc.load_arm(path)
        except (SystemExit, ValueError):
            refused = True
        assert refused, "K14: an arm whose policy lacks the emitter reading must be refused"
    finally:
        os.unlink(path)


def test_k15_room_doubling_fixture_is_the_nearest_emitting_ref():
    """rejects: the doubling listing's fixture chosen by name and origin, missing a neutral-named emitter 29 u away"""
    lb = light(LBX, (0, 0, 0), leak=0.0, lit=1.0, facing=facing(50, 0, 14))
    la = light("000801:ModA.esp", (100, 0, 0), leak=0.0, lit=1.0, facing=facing(50, 0, 14))
    fx = eref(C1, (30, 0, -20), [("emissive", (30, 0, 0), 1)], edid="Prop50")
    arms = {"u": arr([fx], [la, lb]), "plusb": arr([fx], [lb]), "plusa": arr([fx], [la]), "dep": arr([fx], []),
            "p": arr([fx], [la, light(LBX, (0, 0, 0), enabled=False)])}
    r = lc.judge_record(LBX, "DISABLE", arms, MODS, P)
    f = r.get("doublingFixture") or {}
    assert f.get("key") == C1 and abs((f.get("dOwn") or 0) - 29.0) < 1e-6, f"K15: got {f}"


def test_k16_emitting_refs_near_by_part():
    """rejects: the C1b / C3a fixture set chosen by name (a neutral-named emitter missed, a named non-emitter kept)"""
    a = arr([eref(C1, (0, 0, 0), [("emissive", (0, 0, 10), 2)], edid="Prop60"), eref(X1, (5, 0, 0), edid="CandleHornTable01")],
            [light(LBX, (0, 0, 20))])
    near = [k for _, k in lc.emitting_near(a, (0, 0, 20), 64.0)]
    assert near == [C1], f"K16: got {near}"


def test_k17_emissive_value_below_threshold_does_not_emit():
    """rejects: any emissive part counted regardless of its value (Own_Emit is set on nearly every shape; the value decides).
    Added at Gate C as the target of sabotage arm S14."""
    dim = eref(C1, (10, 0, 0), [("emissive", (10, 0, 0), 3)], edid="Prop70")
    dim["emitters"][0]["value"] = 0.01
    assert _host([dim], (0, 0, 0))["host"] is None
    dim["emitters"][0]["value"] = 0.02
    assert _host([dim], (0, 0, 0))["host"] == C1


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
