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


def test_m3_table_sums_and_splits():
    cl = [{"group": "G1", "edits": 7, "reason": "not modelled", "evidenceType": "render reading", "direct": False},
          {"group": "G1", "edits": 3, "reason": "set aside by filter", "evidenceType": "check", "direct": True},
          {"group": "G6", "edits": 5, "reason": "not modelled", "evidenceType": "render reading", "direct": True}]
    rows = M.miss_table(cl)
    assert sum(r["edits"] for r in rows) == 15
    nm = [r for r in rows if r["reason"] == "not modelled"][0]
    assert nm["directEdits"] == 5 and nm["inheritedEdits"] == 7 and nm["edits"] == 12


def main():
    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    for n, f in tests:
        check(n, f)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
