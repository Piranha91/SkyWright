"""Tests for missdiag.py — clustering, the miss-reason procedure and the table (patch-miss packet, GATE-B §2).

Dependency-free: `python _geometry-harness/tests/test_missdiag.py`. Each test asserts one named thing so a sabotage
sweep can point at the assertion that went red. Fixtures are synthetic rows in the scorecard's edits.csv shape; the
FormKeys in the M2 cases are the GATE-A worked examples, used as labels only.
"""

import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import missdiag as M  # noqa: E402

PASSED, FAILURES = [], []


def check(name, fn):
    try:
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, (AssertionError, NotImplementedError)):
            traceback.print_exc()


def E(group, fk, kind):
    return {"group": group, "formKey": fk, "kind": kind, "category": "5"}


# ---- M1: clustering ----
def test_m1a_record_action_merges_kinds():
    assert M.record_action({"ROTATE", "MOVE"}) == "MOVE+ROTATE"
    assert M.record_action({"DISABLE"}) == "DISABLE"


def _fixture():
    edits = [E("G9", "000A01:X.esp", "MOVE"), E("G9", "000A01:X.esp", "ROTATE"),
             E("G9", "000A02:X.esp", "MOVE"), E("G9", "000A02:X.esp", "ROTATE"),
             E("G9", "000A03:X.esp", "MOVE"), E("G9", "000A03:X.esp", "ROTATE"),
             E("G9", "000B01:X.esp", "DISABLE")]
    bases = {("G9", k): "Chair" for k in ("000A01:X.esp", "000A02:X.esp", "000A03:X.esp")}
    bases[("G9", "000B01:X.esp")] = "Chair"
    cells = {("G9", k): "CellOne" for k in ("000A01:X.esp", "000A02:X.esp", "000A03:X.esp", "000B01:X.esp")}
    same = (("contact", "never", "NotMovedOverThreshold"),)
    traces = {("G9", "000A01:X.esp"): same, ("G9", "000A02:X.esp"): same,
              ("G9", "000A03:X.esp"): (("contact", "set_aside", "TooSmallToHost"),),
              ("G9", "000B01:X.esp"): same}
    return edits, bases, cells, traces


def test_m1b_cluster_key_and_split_on_trace():
    edits, bases, cells, traces = _fixture()
    cl = M.build_clusters(edits, bases, cells, traces)
    by = {(c["action"], c["trace"]): c for c in cl}
    a = by[("MOVE+ROTATE", (("contact", "never", "NotMovedOverThreshold"),))]
    assert a["records"] == ["000A01:X.esp", "000A02:X.esp"] and a["edits"] == 4
    b = by[("MOVE+ROTATE", (("contact", "set_aside", "TooSmallToHost"),))]
    assert b["records"] == ["000A03:X.esp"] and b["edits"] == 2, "members disagreeing on a trace outcome must split"
    assert by[("DISABLE", (("contact", "never", "NotMovedOverThreshold"),))]["edits"] == 1
    assert sum(c["edits"] for c in cl) == len(edits)


def test_m1c_representatives():
    edits = [E("G9", "%06X:X.esp" % (0xC00 + i), "DISABLE") for i in range(12)]
    bases = {("G9", e["formKey"]): "Lamp" for e in edits}
    cells = {("G9", e["formKey"]): "C" for e in edits}
    traces = {("G9", e["formKey"]): () for e in edits}
    cl = M.build_clusters(edits, bases, cells, traces)
    assert len(cl) == 1 and cl[0]["representatives"] == ["000C00:X.esp", "000C0B:X.esp"], cl[0]["representatives"]
    small = M.build_clusters(edits[:3], bases, cells, traces)
    assert small[0]["representatives"] == ["000C00:X.esp"]


def test_m1d_render_selection_reaches_80_percent_per_group():
    cl = [{"id": "g1-a", "group": "G1", "edits": 60}, {"id": "g1-b", "group": "G1", "edits": 25},
          {"id": "g1-c", "group": "G1", "edits": 15}, {"id": "g2-a", "group": "G2", "edits": 5},
          {"id": "g2-b", "group": "G2", "edits": 5}]
    ids, cov = M.render_selection(cl, 0.8)
    assert ids == {"g1-a", "g1-b", "g2-a", "g2-b"}, ids
    assert abs(cov["G1"] - 0.85) < 1e-9 and abs(cov["G2"] - 1.0) < 1e-9


# ---- M2: one fixture per reason (GATE-A §4, frozen §12) ----
def _ev(**kw):
    base = {"rendered": True, "likelyClass": "interpenetration", "classEvidence": "render reading",
            "check": "geometry.object-overlap", "checkRunnable": True, "bundleConfiguration": "ran",
            "trace": None, "renderShowsDefect": None}
    base.update(kw)
    return base


def test_m2a_not_modelled():   # G6 08D23A:Lux.esp, FX beam vs a moved opening
    r = M.assign_reason(_ev(likelyClass="fx-alignment", check=None))
    assert r["reason"] == "not modelled" and r["evidenceType"] == "render reading"


def test_m2b_out_of_scope_population():   # G5 00FC9B:SurWR.esp, city's own plane outside the panel population
    r = M.assign_reason(_ev(likelyClass="occluder", check="environment.occlusion",
                            trace={"outcome": "never", "rule": "NotInPanelPopulation"}))
    assert r["reason"] == "out of scope (population)" and r["detail"] == "NotInPanelPopulation"


def test_m2c_not_run_but_caught_when_run():   # G1 0517B9 shrine: overlap skipped in the bundle, run offline
    r = M.assign_reason(_ev(bundleConfiguration="skipped-user",
                            trace={"outcome": "judged_relation", "rule": "crossing", "emitted": True}))
    assert r["reason"] == "out of scope (not run; caught when run)" and r["bundleConfiguration"] == "skipped-user"


def test_m2d_out_of_scope_not_runnable():
    r = M.assign_reason(_ev(likelyClass="route", check="geometry.route-cut", checkRunnable=False))
    assert r["reason"] == "out of scope (not run)"


def test_m2e_set_aside_by_filter():   # G6 0DFED6 x 62B337: crossing removed by pair attribution
    r = M.assign_reason(_ev(trace={"outcome": "set_aside", "rule": "PairAttribution(AxB)"}))
    assert r["reason"] == "set aside by filter" and r["detail"] == "PairAttribution(AxB)"


def test_m2f_below_threshold_numeric_only():
    r = M.assign_reason(_ev(trace={"outcome": "judged_below_threshold", "rule": "NearMiss"}))
    assert r["reason"] == "below threshold" and r["detail"] == "NearMiss"


def test_m2g_not_clash_is_not_below_threshold():   # MovedTogether (M2)
    r = M.assign_reason(_ev(likelyClass="support", check="geometry.placement-contact",
                            trace={"outcome": "judged_relation_not_clash", "rule": "MovedTogether"}, renderShowsDefect=None))
    assert r["reason"] == "taste (no clash)" and r["evidenceType"] == "check"


def test_m2h_taste_mechanical_integration():   # G6 000801 Lux light added into a COTN interior
    r = M.assign_reason(_ev(likelyClass="integration", classEvidence="mechanical", check=None))
    assert r["reason"] == "taste (no clash)" and r["evidenceType"] == "mechanical"


def test_m2i_render_reading_alone_never_certifies_taste():   # H2
    r = M.assign_reason(_ev(likelyClass="integration", classEvidence="render reading", check=None))
    assert r["reason"] == "cannot tell" and "render reading only" in r["detail"]


def test_m2j_judged_nothing_but_render_shows_defect():   # Q4 sub-label
    r = M.assign_reason(_ev(trace={"outcome": "judged_no_relation", "rule": ""}, renderShowsDefect=True))
    assert r["reason"] == "not modelled (judged, saw nothing)" and r["evidenceType"] == "render reading"


def test_m2k_cannot_tell_unjudgeable():   # G6 BE90DA:Lux.esp, degenerate triangle
    r = M.assign_reason(_ev(trace={"outcome": "unjudgeable", "rule": "b_degenerate_triangle"}))
    assert r["reason"] == "cannot tell" and r["detail"] == "b_degenerate_triangle"


def test_m2l_cannot_tell_not_rendered():
    r = M.assign_reason(_ev(rendered=False, likelyClass=None, classEvidence=None))
    assert r["reason"] == "cannot tell" and "not rendered" in r["detail"]


def test_m2m_emitted_in_a_bundle_that_ran_is_flagged_not_joined():   # a detection, not a miss
    r = M.assign_reason(_ev(trace={"outcome": "judged_relation", "rule": "Stranded", "emitted": True,
                                   "notJoined": "join key mismatch"}))
    assert r["reason"] == "flagged, not joined" and r["detail"] == "join key mismatch", r
    assert "flagged, not joined" in M.REASONS


# ---- M3: the table ----
def test_m2n_fix_attribution_gives_fixes_an_inter_mod_clash():   # loose-item clashes: 2B8DCD (g1-0010) label only
    fix = {"check": "loose-item support", "unpatched": "lost (no support below)", "attribution": "needs both",
           "patched": "support restored"}
    r = M.assign_reason(_ev(trace=None, fixAttribution=fix))
    assert r["reason"] == "fixes an inter-mod clash" and r["evidenceType"] == "check"
    assert r["detail"] == "loose-item support: lost (no support below); needs both; patched: support restored"


def test_m2o_no_fix_attribution_keeps_todays_rules():
    assert M.assign_reason(_ev(trace=None))["reason"] == "cannot tell"
    assert M.assign_reason(_ev(trace=None, fixAttribution=None))["reason"] == "cannot tell"


def test_m2p_fix_attribution_must_need_both():
    try:
        M.assign_reason(_ev(fixAttribution={"check": "x", "unpatched": "lost", "attribution": "not inter-mod", "patched": "y"}))
    except ValueError:
        return
    raise AssertionError("an attribution other than 'needs both' was accepted")


def _clip(**kw):
    c = {"check": "loose-item clip at rest", "item": "item A", "other": "object B", "depth": 5.0,
         "pose": "rest pose read in game", "edit": "restores support"}
    c.update(kw)
    return c


def test_m2q_placement_clip_gives_patch_placement_clips():   # loose-item clashes round 2: a patch-placed item clips
    r = M.assign_reason(_ev(trace=None, placementClip=_clip()))
    assert r["reason"] == "patch placement clips" and r["evidenceType"] == "check"
    assert r["detail"] == "loose-item clip at rest: item A clips object B by 5.00 u at rest pose read in game; edit: restores support"


def test_m2r_placement_clip_outranks_fix_attribution():   # the edit restores a support but leaves its item clipping
    fix = {"check": "loose-item support", "unpatched": "lost", "attribution": "needs both", "patched": "support restored"}
    assert M.assign_reason(_ev(trace=None, fixAttribution=fix, placementClip=_clip()))["reason"] == "patch placement clips"


def test_m2s_placement_clip_needs_every_part_and_a_depth():
    for bad in (_clip(depth=0.0), _clip(depth=None), _clip(other=""), _clip(edit=None)):
        try:
            M.assign_reason(_ev(placementClip=bad))
        except ValueError:
            continue
        raise AssertionError("an incomplete placement clip was accepted: %r" % (bad,))


# An anchor body that meets another mod's object at the patched placement: the body reading is 'meets' with no
# clearance, so there is no depth. The fixture reproduces a transfer-test row (a moved plane marker whose anchor body
# meets a tower mesh), labels neutral; the decide passes it as below.
G_ROW_MEET = {"check": "pair measure at the patched placement (anchor body)", "item": "00083C",
              "other": "tower ruin (005E7F)", "depth": 0.0, "relation": "meets",
              "pose": "its patched placement, p-base", "edit": "moves the object"}


def test_m5a_anchor_body_meet_row_is_worded_as_a_meet():
    r = M.assign_reason(_ev(trace=None, placementClip=G_ROW_MEET))
    assert r["reason"] == "patch placement clips" and r["evidenceType"] == "check", repr(r)
    assert r["detail"] == ("pair measure at the patched placement (anchor body): 00083C meets tower ruin (005E7F) "
                           "at its patched placement, p-base (a meet: depth 0); edit: moves the object"), repr(r["detail"])


def test_m5b_synthetic_meet_without_depth_is_a_meet_not_a_crossing():
    r = M.assign_reason(_ev(trace=None, placementClip=_clip(relation="meets", depth=None)))
    assert r["reason"] == "patch placement clips", repr(r)
    assert " meets object B " in r["detail"] and "(a meet: depth 0)" in r["detail"], repr(r["detail"])
    assert " clips " not in r["detail"] and " by " not in r["detail"], repr(r["detail"])


def test_m5c_positive_depth_crossing_keeps_todays_wording():
    for c in (_clip(), _clip(relation="crosses")):
        r = M.assign_reason(_ev(trace=None, placementClip=c))
        assert r["detail"] == "loose-item clip at rest: item A clips object B by 5.00 u at rest pose read in game; edit: restores support", repr(r["detail"])


def test_m5d_a_meet_with_a_depth_or_an_unknown_relation_raises():
    for bad in (_clip(relation="meets"), _clip(relation="meets", depth=0.5), _clip(relation="touches", depth=0.0)):
        try:
            M.assign_reason(_ev(placementClip=bad))
        except ValueError:
            continue
        raise AssertionError("a meet with a depth, or an unknown relation, was accepted: %r" % (bad,))


def test_m3_table_sums_and_splits():
    cl = [{"group": "G1", "edits": 7, "reason": "not modelled", "evidenceType": "render reading", "direct": False},
          {"group": "G1", "edits": 3, "reason": "set aside by filter", "evidenceType": "check", "direct": True},
          {"group": "G6", "edits": 5, "reason": "not modelled", "evidenceType": "render reading", "direct": True}]
    rows = M.miss_table(cl)
    assert sum(r["edits"] for r in rows) == 15
    nm = [r for r in rows if r["reason"] == "not modelled"][0]
    assert nm["directEdits"] == 5 and nm["inheritedEdits"] == 7 and nm["edits"] == 12


# ---- M4: a record no built manifest holds enabled (Group 6 follow-up F-E2) ----
# Known answers from the enable-path lane's repro (2026-10-04, RESULT section 6): three vanilla references the patch
# disables are enabled at load in the unpatched order but outside its responsible-pair manifest; the Lux dust beam and the
# vanilla light are disabled at load. FormKeys are labels only.
OUTSIDE = {"u-base": "not in manifest", "p-base": "absent"}


def _outside_not_disabled(fk):
    b = M.absence_blocker(OUTSIDE, "enabled")
    assert b is not None and b.startswith("outside the responsible-pair manifest"), "%s: %r" % (fk, b)
    assert "not enabled in any built manifest" not in b and "disabled" not in b, "%s read as disabled: %r" % (fk, b)


def test_m4a_vanilla_doorway_enabled_at_load_is_not_disabled():   # 0DFE8F:Skyrim.esm, FarmInt2Doorway03
    _outside_not_disabled("0DFE8F")


def test_m4b_vanilla_end02_enabled_at_load_is_not_disabled():   # 0DFED6:Skyrim.esm, FarmInt2End02
    _outside_not_disabled("0DFED6")


def test_m4c_vanilla_end01_enabled_at_load_is_not_disabled():   # 0DFED7:Skyrim.esm, FarmInt2End01
    _outside_not_disabled("0DFED7")


def test_m4d_disabled_singleton_reads_disabled_at_load():   # 62B31F:Lux.esp, a dust beam disabled at load
    b = M.absence_blocker({"u-base": "absent", "p-base": "absent"}, "disabled")
    assert b is not None and b.startswith("not enabled in any built manifest") and "disabled at load" in b, repr(b)


def test_m4e_unread_enable_state_is_not_called_disabled():
    b = M.absence_blocker(OUTSIDE, None)
    assert b is not None and "not read" in b, repr(b)
    assert "not enabled in any built manifest" not in b and "disabled" not in b, repr(b)


def test_m4f_enabled_in_a_manifest_has_no_absence_blocker():
    assert M.absence_blocker({"u-base": "enabled", "p-base": "absent"}, None) is None
    assert M.absence_blocker({"u-base": "enabled"}, "disabled") is None


def test_m4g_unknown_enable_state_value_raises():
    try:
        M.absence_blocker(OUTSIDE, "maybe")
    except ValueError:
        return
    raise AssertionError("an unknown load-time enable state was accepted")


# ---- occlusion planes: a plane cluster's reason comes from the occlusion assessment (planeassess), never from contact,
# floor or anchor rules (plane-assessment Gate C, fold integration)
def _occ(verdict, statement, **kw):
    return {"occlusion": dict({"verdict": verdict, "statement": statement, "check": "occlusion (planeassess)"}, **kw),
            "bundleConfiguration": "ran"}


def test_o1_occlusion_fix_is_a_fix():
    r = M.assign_reason(_occ("fixes an inter-mod clash", "fixes an inter-mod clash at the 9 route views where it hid"))
    assert (r["reason"], r["evidenceType"]) == ("fixes an inter-mod clash", "check"), r
    assert r["detail"] == "occlusion (planeassess): fixes an inter-mod clash at the 9 route views where it hid", r


def test_o2_occlusion_patch_hides_has_its_own_reason():
    r = M.assign_reason(_occ("patch placement hides", "patch placement hides content at 1 route view"))
    assert r["reason"] == "patch placement hides" and "patch placement hides" in M.REASONS, r


def test_o3_occlusion_set_aside_and_taste_and_cannot_tell():
    assert M.assign_reason(_occ("set aside", "set aside: disabled; nothing hidden at 3 route views"))["reason"] == "set aside by filter"
    assert M.assign_reason(_occ("taste (no clash)", "taste (no clash): nothing hidden at 2 route views"))["reason"] == "taste (no clash)"
    r = M.assign_reason(_occ("cannot tell", "cannot tell: rule refuses a tilted plane"))
    assert (r["reason"], r["evidenceType"]) == ("cannot tell", "check"), r


def test_o4_occlusion_detail_carries_share_and_box_label():
    r = M.assign_reason(_occ("taste (no clash)", "taste (no clash): nothing hidden at 2 route views", unjudgedShare=0.4512,
                             boxLabel="Box verdict: the Box silhouette rule rests on one in-game station"))
    assert r["detail"].endswith("; unjudged share 0.4512; Box verdict: the Box silhouette rule rests on one in-game station"), r


def test_o5_occlusion_unknown_verdict_raises():
    try:
        M.assign_reason(_occ("fixed", "x"))
    except ValueError:
        return
    raise AssertionError("an unknown occlusion verdict must raise")


def test_o6_plane_records_are_recognised():
    assert M.is_plane_record("000017:Skyrim.esm", None) and M.is_plane_record("0ABCDE:Some.esp", {"bounds": [1, 2, 3]})
    assert not M.is_plane_record("0ABCDE:Some.esp", None)


def test_o7_occlusion_does_not_fix_is_a_measured_reason():
    r = M.assign_reason(_occ("does not fix", "does not fix: still hides 400 px of the content mod's content (u 100 px) at 1 route view"))
    assert (r["reason"], r["evidenceType"]) == ("does not fix", "check") and "does not fix" in M.REASONS, r


# ---- S: the group table's summary leads with the prediction, then coverage, then validation against the patch ----
def _summary_doc(prediction=None):
    """Six patch edits on six records, one cluster each (ids g1-0001.. in formKey order), one miss reason apiece, plus
    one edit with no evidence (unassigned)."""
    fks = ["00000%d:Patch.esp" % i for i in range(1, 8)]
    edits = [E("G1", fk, "MOVE") for fk in fks]
    records = {"G1|" + fk: {"base": "base%d" % i, "cell": "cell%d" % i, "trace": []} for i, fk in enumerate(fks)}
    ev = {
        "g1-0001": {"fixAttribution": {"check": "contact", "unpatched": "item floats", "attribution": "needs both",
                                       "patched": "support restored"}, "bundleConfiguration": "ran", "direct": True},
        "g1-0002": {"likelyClass": "lighting", "classEvidence": "mechanical", "check": None, "rendered": True,
                    "bundleConfiguration": "ran", "direct": True},
        "g1-0003": {"likelyClass": "contact", "classEvidence": "check", "check": "contact", "checkRunnable": False,
                    "whyNotRunnable": "needs the running game", "bundleConfiguration": "ran", "direct": True},
        "g1-0004": {"likelyClass": "contact", "classEvidence": "check", "check": "contact", "checkRunnable": True,
                    "trace": {"outcome": "judged_no_relation", "rule": "no support relation"}, "renderShowsDefect": None,
                    "bundleConfiguration": "ran", "direct": True},
        "g1-0005": {"likelyClass": "contact", "classEvidence": "check", "check": "contact", "checkRunnable": True,
                    "trace": {"outcome": "set_aside", "rule": "decorative clutter"}, "bundleConfiguration": "ran",
                    "direct": True},
        "g1-0006": {"placementClip": {"check": "contact", "item": "a crate", "other": "a wall of the other mod",
                                      "depth": 4.0, "pose": "rest pose", "edit": "adds the item"},
                    "bundleConfiguration": "ran", "direct": True},
    }
    doc = {"edits": edits, "records": records, "evidence": ev}
    if prediction is not None:
        doc["prediction"] = prediction
    return doc


def test_s1_summary_leads_with_prediction_then_coverage_then_validation():
    s = M.run(_summary_doc()).get("summary")
    assert s is not None, "run() writes no summary: the group table has no prediction-first reading"
    assert [x.get("id") for x in s.get("sections") or []] == ["prediction", "coverage", "validation"], s


def test_s2_missing_prediction_is_said_not_silent():
    s = M.run(_summary_doc()).get("summary") or {}
    pred = next((x for x in s.get("sections") or [] if x.get("id") == "prediction"), None)
    assert pred is not None and pred.get("status") == "not-supplied" and str(pred.get("missingFact") or "").strip(), pred


def test_s3_blind_spots_carry_their_why():
    s = M.run(_summary_doc()).get("summary") or {}
    val = next((x for x in s.get("sections") or [] if x.get("id") == "validation"), {})
    spots = val.get("blindSpots") or []
    assert sorted(b.get("reason") for b in spots) == ["not modelled", "out of scope (not run)"], spots
    assert all(str(b.get("why") or "").strip() for b in spots), spots
    assert any("needs the running game" in b["why"] for b in spots), spots


def test_s4_hits_taste_and_patch_problems_are_not_blind_spots():
    s = M.run(_summary_doc()).get("summary") or {}
    val = next((x for x in s.get("sections") or [] if x.get("id") == "validation"), {})
    assert val.get("hits") == 1 and val.get("tasteOrSetAside") == 2 and val.get("patchProblems") == 1, val
    cov = next((x for x in s.get("sections") or [] if x.get("id") == "coverage"), {})
    assert cov.get("unassignedEdits") == 1, cov


def test_s5_open_comes_from_the_prediction():
    pred = {"mods": ["ModA.esp", "ModB.esp"],
            "clashes": [{"kind": "contact", "inGame": "an object left floating", "needsBoth": 28, "actedOnByPatch": 25}]}
    s = M.run(_summary_doc(pred)).get("summary") or {}
    sec = {x.get("id"): x for x in s.get("sections") or []}
    p, val = sec.get("prediction") or {}, sec.get("validation") or {}
    assert p.get("status") == "found" and [c.get("needsBoth") for c in p.get("clashes") or []] == [28], p
    assert val.get("open") == 3, val


def test_s6_open_without_a_prediction_is_said():
    s = M.run(_summary_doc()).get("summary") or {}
    val = next((x for x in s.get("sections") or [] if x.get("id") == "validation"), {})
    assert val.get("open") is None and any(str(x).strip() for x in val.get("missingFacts") or []), val


# ---- P: a tracked producer of the prediction block, from a scorecard group's needs-both counts ----
def _scorecard_row(group="G9"):
    """One group row in the scorecard's shape: the two mods and, per lane, the rows on the unpatched order, how many
    need both mods, and how many of those the patch acts on."""
    return {"group": group, "modA": "ModA.esp", "modB": "ModB.esp",
            "findingsByLane": {"contact": {"rows": 40, "needsBoth": 28, "needsBothActedOn": 25, "preFixBuild": True},
                               "record": {"rows": 9, "needsBoth": 5, "needsBothActedOn": 2,
                                          "unit": "distinct (FormKey, field path)"},
                               "seam": {"rows": 3, "needsBoth": 0, "needsBothActedOn": 0}}}


def test_p1_prediction_from_a_scorecard_group_row():
    p = M.prediction_from_scorecard(_scorecard_row())
    assert p.get("mods") == ["ModA.esp", "ModB.esp"], p
    got = [(c.get("kind"), c.get("needsBoth"), c.get("actedOnByPatch")) for c in p.get("clashes") or []]
    assert got == [("contact", 28, 25), ("record", 5, 2), ("seam", 0, 0)], got
    rec = next(c for c in p["clashes"] if c["kind"] == "record")
    assert rec.get("unit") == "distinct (FormKey, field path)", rec
    s = M.run(_summary_doc(p)).get("summary") or {}
    sec = {x.get("id"): x for x in s.get("sections") or []}
    assert sec["prediction"].get("status") == "found" and sec["prediction"].get("total") == 33, sec["prediction"]
    assert sec["validation"].get("open") == 6, sec["validation"]


def test_p2_a_row_without_needs_both_counts_is_refused():
    for bad, field in (({k: v for k, v in _scorecard_row().items() if k != "findingsByLane"}, "findingsByLane"),
                       ({**_scorecard_row(), "findingsByLane": {"contact": {"rows": 4}}}, "needsBoth"),
                       ({k: v for k, v in _scorecard_row().items() if k != "modB"}, "modB")):
        try:
            M.prediction_from_scorecard(bad)
        except ValueError as e:
            assert field in str(e) and str(e).strip().endswith("."), str(e)
            continue
        raise AssertionError(f"a row without {field} gave a prediction instead of a refusal")


def _cli(tmp, doc, scorecard, *flags):
    import json
    inp, out, sc = (os.path.join(tmp, n) for n in ("in.json", "out.json", "scorecard.json"))
    with open(inp, "w", encoding="utf-8") as f:
        json.dump(doc, f)
    with open(sc, "w", encoding="utf-8") as f:
        json.dump(scorecard, f)
    os.environ["HOUSECARL_RUNLOG_DIR"] = os.path.join(tmp, "runlog")
    code = M.main(["missdiag.py", inp, out] + [sc if x == "{sc}" else x for x in flags])
    res = None
    if os.path.exists(out):
        with open(out, encoding="utf-8") as f:
            res = json.load(f)
    return code, res


def test_p3_command_line_reads_the_prediction_from_a_scorecard():
    import tempfile
    sc = {"groups": [_scorecard_row("G8"), _scorecard_row("G9")]}
    with tempfile.TemporaryDirectory() as tmp:
        code, res = _cli(tmp, _summary_doc(), sc, "--scorecard", "{sc}", "--group", "G9")
        assert code == 0 and res is not None, code
        pred = next(x for x in res["summary"]["sections"] if x["id"] == "prediction")
        assert pred.get("status") == "found" and pred.get("total") == 33, pred
    with tempfile.TemporaryDirectory() as tmp:
        code, res = _cli(tmp, _summary_doc(), sc, "--scorecard", "{sc}", "--group", "G7")
        assert code == 2 and res is None, (code, res)
    with tempfile.TemporaryDirectory() as tmp:
        own = M.prediction_from_scorecard(_scorecard_row())
        code, res = _cli(tmp, _summary_doc(own), sc, "--scorecard", "{sc}", "--group", "G9")
        assert code == 2 and res is None, (code, res)


def main():
    tests =[(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
