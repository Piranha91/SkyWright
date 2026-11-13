#!/usr/bin/env python
"""Checks that a report joins more than one separate evaluation of the same order (`plan --sibling-bundle` given
several times), each lending the setups it can stand in for, and refuses one it cannot use, by name.

The case is a mod evaluated against a block of neighbours as several small two-arm evaluations, as Missives was:
  main     the setup without the patch (P) against the setup with the patch;
  sibling  P against P without the evaluated mod (it holds P itself, so its checks are compared row for row there);
  sibling  the evaluated mod with the dependency only against the dependency only (it holds no P: its setups are P
           with plugins left out, same bytes, same population and scope).
A clash between the evaluated mod and a neighbour that the second sibling also shows with no neighbour loaded comes
from the evaluated mod; one it does not show needs both. With only the first sibling the report can only say that
whether it needs the neighbour was not measured.

Every cell runs the real `plan` command in this process (verify-bundle stubbed, since the bundles are hand-built) on
bundles from test_evalreport_prediction's builder, renders the real templates, and asserts on the plan and the page.

Usage:
  python _geometry-harness/tests/test_evalreport_siblings.py
Exit 0 all pass, 1 a cell failed. HC_CELLS=a,b runs the cells whose names contain a or b.
"""
from __future__ import annotations

import contextlib
import io
import json
import os
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import test_evalreport_prediction as T                  # noqa: E402  (sets the no-settings environment first)

R = T.R
R.run_verify_bundle = lambda bundle, out, copy: ({"bundle": {"complete": True, "schemaSupported": True}}, 0, "")
os.environ["HOUSECARL_RUNLOG_DIR"] = str(Path(tempfile.gettempdir()) / "hc-tests-evalreport-siblings-runlog")

C, BASE, A, B = T.CONTACT, T.BASE, T.MOD_A, T.MOD_B
DEP, NB1, NB2 = "Dependency.esp", "Neighbour One.esp", "Neighbour Two.esp"
PATCH = "ModA - Neighbour patch.esp"
VANILLA = "000901:Skyrim.esm"                   # a base-game object a neighbour edits
NB_OBJ = f"000902:{NB1}"                        # the neighbour's own object
COMBINED_DIR, ALONE_DIR = "eval-combined-vs-neighbours", "eval-alone-vs-dependency"


def against_vanilla(a):
    return T.row("CONTACT", a, A, f"000801:{A}", other=VANILLA)


def against_neighbour():
    return T.row("CONTACT", NB1, A, f"000802:{A}", other=NB_OBJ)


def main_spec(order):
    """The evaluation with the patch: P against P plus the patch, both clashes measured on P."""
    arms = [T.arm("baseline", order, kind="baseline"), T.arm("patched", order + [PATCH])]
    return ("main", arms, {(C, "baseline"): [against_vanilla(NB1), against_neighbour()], (C, "patched"): []},
            [A], {"patches": [PATCH]})


def combined_spec(order, name=COMBINED_DIR, plan_id="sibcombined0", without_rows=()):
    """P against P without the evaluated mod: holds P, measured the same rows there."""
    arms = [T.arm("baseline", order, kind="baseline"), T.arm("neighbours-only", [x for x in order if x != A])]
    return (name, arms, {(C, "baseline"): [against_vanilla(NB1), against_neighbour()],
                         (C, "neighbours-only"): list(without_rows)}, [A], {"plan_id": plan_id})


def alone_spec(name=ALONE_DIR, plan_id="sibalone0000", shows_vanilla=True, shas=None, setup=None, extra=None,
               more_results=None):
    """The evaluated mod with the dependency only against the dependency only: holds no P. shas: other bytes for these
    plugins in every setup; extra: further make_bundle arguments; more_results: further rows by (check, arm)."""
    setup = setup or [BASE, DEP, A]
    arms = [T.arm("baseline", setup, kind="baseline", shas=shas),
            T.arm("dependency-only", [x for x in setup if x != A], shas=shas)]
    rows = [against_vanilla(BASE)] if shows_vanilla else []
    return (name, arms, {(C, "baseline"): rows, (C, "dependency-only"): [], **(more_results or {})}, [A],
            {"plan_id": plan_id, **(extra or {})})


BLOCK = [BASE, DEP, NB1, NB2, A]                 # the Missives shape: the mod alone lacks every neighbour
ONE_NEIGHBOUR = [BASE, DEP, NB1, A]              # the mod alone is P without the one neighbour, exactly


def run_plan(main, siblings, declare=(PATCH,)):
    """The real `plan` command on hand-built bundles, then the real templates: a dict with the exit code, the refusal
    text, the plan, report.md, report.html and the evidence index (None when refused)."""
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        dirs = {}
        for spec in [main, *siblings]:
            name, arms, results, targets, kw = spec
            if name not in dirs:
                kw = dict(kw)
                files = kw.pop("_files", None) or {}          # further bundle files, {relative path: JSON document}
                dirs[name] = T.make_bundle(td / name, arms, results, targets, **kw)
                for rel_, doc_ in files.items():
                    (dirs[name] / rel_).parent.mkdir(parents=True, exist_ok=True)
                    (dirs[name] / rel_).write_text(json.dumps(doc_), encoding="utf-8")
        out = td / "out"
        args = ["plan", "--bundle", str(dirs[main[0]]), "--out", str(out)]
        for p_ in declare:
            args += ["--declare-patch", p_]
        for s_ in siblings:
            args += ["--sibling-bundle", str(dirs[s_[0]])]
        err, sink = io.StringIO(), io.StringIO()
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(sink):
            code = R.cmd_plan(R.build_parser().parse_args(args))
        res = {"code": code, "err": err.getvalue().strip(), "dirs": {k: str(v) for k, v in dirs.items()},
               "plan": None, "md": None, "html": None, "index": None}
        pres = out / "presentation.json"
        if code == 0 and pres.exists():
            plan = json.loads(pres.read_text(encoding="utf-8"))
            pr = T.priorities()
            s_ = plan.get("settings") or {}
            view = R.effective_view(plan, s_.get("rowsPerGroup", 3), s_.get("figuresPerFinding", 1), pr, "normal")
            rendered = R.render_all(plan, view, td / "rendered", "")
            res.update(plan=plan, md=rendered["report.md"], html=rendered["report.html"],
                       index=rendered["evidence-index.md"])
        return res


def prediction(res):
    return (T.reader(res["plan"]).get("prediction") or {}) if res["plan"] else {}


def attributions(res):
    """formKey prefix -> attribution of each admitted row the prediction record holds."""
    return {(e.get("formKey") or "")[:6]: e.get("attribution") for e in prediction(res).get("record") or []
            if e.get("admission") == "admitted"}


def page_text(res):
    return T.section_text(res["md"], T.headings(res["plan"]).get("prediction") or "\0") if res["md"] else ""


def refused(res):
    return f"plan exit {res['code']}: {res['err'][:300]}" if res["code"] != 0 else ""


def words():
    return (T.priorities().get("reader") or {}).get("prediction") or {}


def sibling_records(res):
    return prediction(res).get("siblings")


WANT = {"000801": "single-mod", "000802": "needs-both"}


def main():
    cells = []

    def cell(name, fn):
        only_ = os.environ.get("HC_CELLS")
        if only_ and not any(x_ and x_ in name for x_ in only_.split(",")):
            return
        try:
            ok, detail = fn()
        except Exception as ex:                                       # a crash is a failure, never a skip
            ok, detail = False, f"raised {type(ex).__name__}: {ex}"
            traceback.print_exc()
        cells.append((name, bool(ok), detail))

    # S1. The second sibling's setup with the evaluated mod and no neighbour answers whether a clash needs the
    # neighbour (finding P82): the vanilla-object clash comes from ModA, the neighbour-object clash needs both.
    def second_sibling_fills_the_partner_role():
        res = run_plan(main_spec(BLOCK), [alone_spec(), combined_spec(BLOCK)])
        if refused(res):
            return False, refused(res)
        got, text = attributions(res), page_text(res)
        unmeasured = f"whether it also needs {NB1} was not measured"
        # (A2) the setup that decided leaves out both neighbours: the page names it, never "the setup without" one
        named_ = f"the setup measured with only {DEP} and {A} besides the base game"
        wrong_ = f"the setup without {NB1}"
        ok = (got == WANT and unmeasured not in text and f"It needs both {A} and {NB1}" in text
              and f"It does not need {NB1}" in text and text.count(named_) == 2 and wrong_ not in text
              and f"without {NB1} too" not in text)
        return ok, (f"attributions {got} (want {WANT}); 'not measured' on the page: {unmeasured in text}; "
                    f"'needs both' on the page: {f'It needs both {A} and {NB1}' in text}; the deciding setup named "
                    f"{text.count(named_)} times (want 2); {wrong_!r} on the page: {wrong_ in text}")

    # S2. The order the siblings are given in does not change what the report says.
    def sibling_order_does_not_change_the_report():
        one = run_plan(main_spec(BLOCK), [alone_spec(), combined_spec(BLOCK)])
        two = run_plan(main_spec(BLOCK), [combined_spec(BLOCK), alone_spec()])
        if refused(two):
            return False, "combined first: " + refused(two)
        if refused(one):
            return False, "alone first: " + refused(one)
        ok = attributions(one) == attributions(two) == WANT and page_text(one) == page_text(two)
        return ok, (f"alone first {attributions(one)}; combined first {attributions(two)}; "
                    f"same prediction text: {page_text(one) == page_text(two)}")

    # S3. A second sibling whose setup is P without the one neighbour, exactly, stands in for the setup without it.
    def second_sibling_fills_the_setup_without_the_partner():
        res = run_plan(main_spec(ONE_NEIGHBOUR), [alone_spec(), combined_spec(ONE_NEIGHBOUR)])
        if refused(res):
            return False, refused(res)
        got = attributions(res)
        partners = prediction(res).get("withoutPartnerArms") or {}
        ok = got == WANT and NB1 in partners
        return ok, f"attributions {got} (want {WANT}); setups without a partner {partners}"

    # S4. Two evaluated mods: the second sibling's setups with each mod alone separate a clash the first sibling (both
    # mods, neither) cannot.
    def second_sibling_fills_each_mod_alone():
        patched = [T.row("CONTACT", B, A, "000803:" + A), T.row("CONTACT", T.PATCH, A, f"000E01:{T.PATCH}")]
        main_ = ("main", T.PATCHED_ARMS, {(C, "baseline"): patched, (C, "without-patch"): T.UNPATCHED_ROWS},
                 [A, B], {"patches": [T.PATCH]})
        both = ("eval-both-vs-neither", [T.arm("baseline", [BASE, A, B], kind="baseline"),
                                         T.arm("dependency-only", [BASE])],
                {(C, "baseline"): T.UNPATCHED_ROWS, (C, "dependency-only"): []}, [A, B], {"plan_id": "sibboth00000"})
        each = ("eval-each-alone", [T.arm("baseline", [BASE, A], kind="baseline"), T.arm("plus-b", [BASE, B])],
                {(C, "baseline"): [T.row("CONTACT", BASE, A, f"000A01:{A}")], (C, "plus-b"): []}, [A, B],
                {"plan_id": "sibeach00000"})
        res = run_plan(main_, [each, both], declare=(T.PATCH,))
        if refused(res):
            return False, refused(res)
        items = [(i.get("checkId"), i.get("count"), i.get("attribution")) for i in prediction(res).get("items") or []]
        singles = prediction(res).get("singleArms") or {}
        ok = items == [(C, 3, "needs-both")] and all(singles.get(t) for t in (A, B))
        return ok, f"items {items} (want [({C!r}, 3, 'needs-both')]); setups with each mod alone {singles}"

    # S5. The report names which separate evaluation each borrowed setup came from: the plan records every sibling in
    # the order given, with the roles it filled, and the page's source sentence names each evaluation's folder; the
    # evidence index names their plans; the page names no plan id.
    def report_names_the_evaluation_each_setup_came_from():
        res = run_plan(main_spec(BLOCK), [alone_spec(), combined_spec(BLOCK)])
        if refused(res):
            return False, refused(res)
        recs = sibling_records(res)
        if not isinstance(recs, list):
            return False, f"the prediction records no list of siblings (siblings = {recs!r})"
        paths = [str(r_.get("pathAsGiven") or "") for r_ in recs]
        roles = [r_.get("roles") or {} for r_ in recs]
        src = str(prediction(res).get("siblingSource") or "")
        ok = (len(recs) == 2 and paths[0].endswith(ALONE_DIR) and paths[1].endswith(COMBINED_DIR)
              and roles[1].get("without the evaluated mods") == "neighbours-only"
              and "baseline" in roles[0].values()
              and [r_.get("joinBasis") for r_ in recs] == ["sub-order", "shared-setup"]
              and COMBINED_DIR in src and ALONE_DIR in src and src in res["md"]
              and "sibcombined0" in res["index"] and "sibalone0000" in res["index"]
              and "sibcombined0" not in res["md"] and "sibalone0000" not in res["md"])
        return ok, (f"siblings {[(p_[-30:], r_) for p_, r_ in zip(paths, roles)]}; bases "
                    f"{[r_.get('joinBasis') for r_ in recs]}; source sentence {src[:200]!r}")

    # S6. A further sibling none of whose setups is P or P with plugins left out (its ModA has other bytes) is refused
    # in one sentence that names it, and nothing is joined.
    def foreign_second_sibling_refused_by_name():
        shas = {A: "a" * 64, DEP: "d" * 64}
        res = run_plan(main_spec(BLOCK), [combined_spec(BLOCK), alone_spec("eval-foreign", "sibforeign00", shas=shas)])
        if res["code"] == 0:
            return False, "a sibling with no usable setup was accepted"
        err = res["err"].removeprefix("REFUSED: ")
        ok = (err.startswith("sibling-foreign:") and "eval-foreign" in err and COMBINED_DIR not in err
              and err.rstrip(".").count(". ") == 0)
        return ok, f"exit {res['code']}: {err[:300]!r}"

    # S7. Siblings none of which holds P itself are refused: no check could be compared between the evaluations.
    def no_sibling_holds_the_shared_setup_refused():
        res = run_plan(main_spec(BLOCK), [alone_spec(), alone_spec("eval-alone-again", "sibalone0002")])
        if res["code"] == 0:
            return False, "siblings none of which holds the setup without the patch were joined"
        err = res["err"].removeprefix("REFUSED: ")
        ok = err.startswith("sibling-unanchored:") and err.rstrip(".").count(". ") == 0
        return ok, f"exit {res['code']}: {err[:300]!r}"

    # S8. The same evaluation given twice is refused, not counted twice.
    def same_sibling_twice_refused():
        res = run_plan(main_spec(BLOCK), [combined_spec(BLOCK), combined_spec(BLOCK)])
        if res["code"] == 0:
            return False, "the same sibling bundle given twice was accepted"
        err = res["err"].removeprefix("REFUSED: ")
        ok = err.startswith("sibling-repeated:") and COMBINED_DIR in err
        return ok, f"exit {res['code']}: {err[:300]!r}"

    # S9. Two siblings offer the same setup and measured the same there: the first given fills the role, and the other
    # is recorded as offering it, with where the setup was taken from.
    def two_siblings_offer_one_setup_first_given_fills_it():
        res = run_plan(main_spec(BLOCK), [alone_spec(), alone_spec("eval-alone-copy", "sibalonecopy"),
                                          combined_spec(BLOCK)])
        if refused(res):
            return False, refused(res)
        recs = sibling_records(res)
        if not isinstance(recs, list) or len(recs) != 3:
            return False, f"siblings recorded: {recs!r}; attributions {attributions(res)}"
        offered = recs[1].get("offered") or {}
        used_from = {str(v_.get("usedFrom") or "") for v_ in offered.values()} if isinstance(offered, dict) else set()
        ok = (attributions(res) == WANT and "baseline" in (recs[0].get("roles") or {}).values()
              and not recs[1].get("roles") and bool(offered) and all(u_.endswith(ALONE_DIR) for u_ in used_from))
        return ok, (f"attributions {attributions(res)}; first roles {recs[0].get('roles')}; second roles "
                    f"{recs[1].get('roles')}, offered {offered}")

    # S10. Two siblings measured the same setup differently for a check: neither is used for it, and the page says why.
    def two_siblings_measure_one_setup_differently_not_separable():
        res = run_plan(main_spec(BLOCK), [alone_spec(), alone_spec("eval-alone-other", "sibaloneothr",
                                                                   shows_vanilla=False), combined_spec(BLOCK)])
        if refused(res):
            return False, refused(res)
        why = (words().get("notSeparableWhy") or {}).get("siblingsDisagree")
        got = attributions(res)
        text = page_text(res)
        ok = bool(why) and set(got.values()) == {"not-separable"} and why in text
        return ok, (f"attributions {got} (want both not-separable); notSeparableWhy.siblingsDisagree words "
                    f"{'present' if why else 'absent'}, on the page: {bool(why) and why in text}")

    # S11. A sibling holding P takes a role before one that holds only P with plugins left out, whatever the order.
    def sibling_holding_the_shared_setup_takes_precedence():
        # P without ModA and the dependency alone: no P, so it holds the setup without the evaluated mod as a sub-order
        w_only = ("eval-neighbours-alone", [T.arm("baseline", [BASE, DEP, NB1, NB2], kind="baseline"),
                                            T.arm("dependency-only", [BASE, DEP])],
                  {(C, "baseline"): [], (C, "dependency-only"): []}, [A], {"plan_id": "sibneighbours"})
        res = run_plan(main_spec(BLOCK), [w_only, combined_spec(BLOCK)])
        if refused(res):
            return False, refused(res)
        recs = sibling_records(res)
        if not isinstance(recs, list) or len(recs) != 2:
            return False, f"siblings recorded: {recs!r}"
        role = "without the evaluated mods"
        ok = ((recs[1].get("roles") or {}).get(role) == "neighbours-only" and role not in (recs[0].get("roles") or {})
              and str(((recs[0].get("offered") or {}).get(role) or {}).get("usedFrom") or "").endswith(COMBINED_DIR))
        return ok, f"first {recs[0].get('roles')} offered {recs[0].get('offered')}; second {recs[1].get('roles')}"

    # S12 (A1). A further sibling's setup with a kept plugin of other bytes is not used: its ModA differs, so the setup
    # with ModA alone stands in for nothing; the items stay as with the first sibling alone and the record says why.
    def kept_plugin_other_bytes_not_used():
        res = run_plan(main_spec(BLOCK), [alone_spec("eval-alone-other-bytes", "sibalonebyte", shas={A: "a" * 64}),
                                          combined_spec(BLOCK)])
        if refused(res):
            return False, refused(res)
        recs = sibling_records(res)
        got = attributions(res)
        if not isinstance(recs, list) or len(recs) != 2:
            return False, f"siblings recorded: {recs!r}; attributions {got}"
        why_ = str((recs[0].get("unusableSetups") or {}).get("baseline") or "")
        ok = (got == {"000801": "with-target", "000802": "with-target"} and not recs[0].get("roles")
              and A in why_ and "other bytes" in why_ and "dependency-only" not in (recs[0].get("unusableSetups") or {}))
        return ok, f"attributions {got}; its roles {recs[0].get('roles')}; unusable setups {recs[0].get('unusableSetups')}"

    # S13 (A1). A further sibling holding only P with plugins left out whose location scope does not cover this
    # evaluation's (it measured one cell; this one measured the whole order) is refused by name, saying why.
    def scope_not_covering_refused():
        scope_ = {"schemaVersion": 1, "complete": True, "locationContributors": {"complete": True, "locations": [
            {"cell": "000D74:Skyrim.esm", "kind": "exterior", "worldspace": "Tamriel", "x": 0, "y": 0, "plugins": []}]}}
        res = run_plan(main_spec(BLOCK), [alone_spec("eval-alone-scoped", "sibalonescop", extra={
            "manifest_extra": {"scope": "scope/scope.json"}, "_files": {"scope/scope.json": scope_}}),
            combined_spec(BLOCK)])
        if res["code"] == 0:
            return False, (f"a sibling whose scope does not cover this evaluation's was joined; attributions "
                           f"{attributions(res)}")
        err = res["err"].removeprefix("REFUSED: ")
        ok = (err.startswith("sibling-foreign:") and "eval-alone-scoped" in err and "location scope" in err
              and err.rstrip(".").count(". ") == 0)
        return ok, f"exit {res['code']}: {err[:300]!r}"

    # S14 (A1). One check whose probe build the further sibling's receipt records in no comparable way is not joined
    # from it (its item stays "not measured"); the other check is joined and separated.
    def one_check_build_not_comparable_others_joined():
        ov = T.OVERLAP
        ov_row = T.row("OVERLAP", NB1, A, f"000803:{A}", other=VANILLA)
        main_ = main_spec(BLOCK)
        main_ = (main_[0], main_[1], {**main_[2], (ov, "baseline"): [ov_row], (ov, "patched"): []}, main_[3], main_[4])
        comb_ = combined_spec(BLOCK)
        comb_ = (comb_[0], comb_[1], {**comb_[2], (ov, "baseline"): [ov_row], (ov, "neighbours-only"): []},
                 comb_[3], comb_[4])
        alone_ = alone_spec(more_results={(ov, "baseline"): [T.row("OVERLAP", BASE, A, f"000803:{A}", other=VANILLA)],
                                          (ov, "dependency-only"): []},
                            extra={"tool_builds": {(ov, "baseline"): None}})
        res = run_plan(main_, [alone_, comb_])
        if refused(res):
            return False, refused(res)
        got = attributions(res)
        recs = sibling_records(res)
        if not isinstance(recs, list) or len(recs) != 2:
            return False, f"siblings recorded: {recs!r}; attributions {got}"
        joins = {c_.get("checkId"): (c_.get("joined"), c_.get("buildBasis")) for c_ in recs[0].get("checks") or []}
        ok = (got == {"000801": "single-mod", "000802": "needs-both", "000803": "with-target"}
              and joins.get(C, (None,))[0] is True and joins.get(ov) == (False, "not-comparable"))
        return ok, f"attributions {got}; its checks {joins}"

    # S15 (round 2, F1). Absence on a setup that leaves out the partner and more shows only that the clash needs one or
    # more of the plugins it leaves out: "needs both" only where the row's other side is the partner's own record. A
    # record field of a base-game record, and an object against a base-game object, are "needs one or more"; an
    # object against the partner's own object stays "needs both".
    def fewer_setup_absence_needs_some_unless_partner_owns():
        lost_ = T.lost_row(NB1, A, "000E01:Skyrim.esm", "MaxHeight", "n", "a", "v")
        vanilla_ = T.row("CONTACT", NB1, A, f"000804:{A}", other="000904:Skyrim.esm")
        main_ = main_spec(BLOCK)
        main_ = (main_[0], main_[1], {**main_[2], (C, "baseline"): main_[2][(C, "baseline")] + [vanilla_],
                                       (T.RECORDS, "baseline"): [lost_], (T.RECORDS, "patched"): []},
                 main_[3], main_[4])
        comb_ = combined_spec(BLOCK)
        comb_ = (comb_[0], comb_[1], {**comb_[2], (C, "baseline"): comb_[2][(C, "baseline")] + [vanilla_],
                                       (T.RECORDS, "baseline"): [lost_], (T.RECORDS, "neighbours-only"): []},
                 comb_[3], comb_[4])
        alone_ = alone_spec(more_results={(T.RECORDS, "baseline"): [], (T.RECORDS, "dependency-only"): []})
        res = run_plan(main_, [alone_, comb_])
        if refused(res):
            return False, refused(res)
        got = attributions(res)
        text = page_text(res)
        some_ = (words().get("attribution") or {}).get("needs-some")
        want = {"000801": "single-mod", "000802": "needs-both", "000804": "needs-some", "000E01": "needs-some"}
        ok = (got == want and bool(some_) and f"It needs both {A} and {NB1}" in text
              and text.count(f"It needs {A} and at least one more mod") == 2)
        return ok, (f"attributions {got} (want {want}); needs-some words {'present' if some_ else 'absent'}; "
                    f"'needs ... at least one more' on the page {text.count(f'It needs {A} and at least one more mod')} "
                    f"times (want 2)")

    # S16 (round 2, F2). A check the sibling holding the shared setup could not join (its rows differ there) is not
    # joined from a sub-order sibling either, which compares no rows: its item stays "not measured", the record says
    # why; a check the shared-setup sibling joined is joined from the sub-order sibling as before.
    def sub_order_check_needs_a_shared_setup_join():
        ov = T.OVERLAP
        ov_row = T.row("OVERLAP", NB1, A, f"000803:{A}", other=VANILLA)
        main_ = main_spec(BLOCK)
        main_ = (main_[0], main_[1], {**main_[2], (ov, "baseline"): [ov_row], (ov, "patched"): []}, main_[3], main_[4])
        comb_ = combined_spec(BLOCK)
        comb_ = (comb_[0], comb_[1], {**comb_[2], (ov, "baseline"): [], (ov, "neighbours-only"): []},
                 comb_[3], comb_[4])                       # measured the overlap differently on the shared setup
        alone_ = alone_spec(more_results={(ov, "baseline"): [T.row("OVERLAP", BASE, A, f"000803:{A}", other=VANILLA)],
                                          (ov, "dependency-only"): []})
        res = run_plan(main_, [alone_, comb_])
        if refused(res):
            return False, refused(res)
        got = attributions(res)
        recs = sibling_records(res)
        if not isinstance(recs, list) or len(recs) != 2:
            return False, f"siblings recorded: {recs!r}; attributions {got}"
        joins = {c_.get("checkId"): (c_.get("joined"), c_.get("why")) for c_ in recs[0].get("checks") or []}
        prim_ = {c_.get("checkId"): c_.get("joined") for c_ in recs[1].get("checks") or []}
        ok = (got.get("000801") == "single-mod" and got.get("000803") == "with-target" and prim_.get(ov) is False
              and joins.get(C, (None,))[0] is True and joins.get(ov, (None,))[0] is False
              and "shared setup" in str(joins.get(ov, (None, ""))[1] or ""))
        return ok, f"attributions {got}; shared-setup sibling joined {prim_}; sub-order sibling checks {joins}"

    # S17-S20, G2-G4 (only-t overclaim): with several evaluated mods, "needs both" only where every evaluated mod is
    # shown needed -- it owns a record on the row, or another mod's "only" setup leaves out exactly it. An "only" setup
    # that leaves out the other mod and more (a plugin taken as its companion, or a third evaluated mod) separates
    # nothing: not-separable, naming that setup. Rows: 801 ModA's object against a base-game object, 802 against ModB's
    # own object, 803 like 801 but there with ModA alone.
    m_rows = [T.row("CONTACT", B, A, f"000801:{A}", other="000901:Skyrim.esm"),
              T.row("CONTACT", B, A, f"000802:{A}", other=f"000902:{B}"),
              T.row("CONTACT", B, A, f"000803:{A}", other="000903:Skyrim.esm")]
    unrel = "Unrelated.esp"

    def multi_main(arms, rows_by_arm, targets):
        plan, md, _ = T.compile_and_render(arms, {(C, a_): r_ for a_, r_ in rows_by_arm.items()}, targets)
        return {"plan": plan, "md": md, "code": 0, "err": ""}

    def leaves_more_why(setup, other):
        return T.R.bind((words().get("notSeparableWhy") or {}).get("onlyLeavesOutMore") or "\0",
                        {"setup": setup, "other": other})

    def multi_check(res, want, why_=None, why_count=0):
        got, md = attributions(res), res["md"] or ""
        ok = got == want and (why_ is None or md.count(why_) == why_count)
        return ok, (f"attributions {got} (want {want})"
                    + (f"; the why naming the setup on the page {md.count(why_)} times (want {why_count})"
                       if why_ is not None else ""))

    # S17. "Only ModA" lacks ModB and a plugin loaded in exactly the setups with ModB (taken as its companion).
    def only_setup_with_companion_not_separable():
        arms = [T.arm("baseline", [BASE, unrel, A, B], kind="baseline"), T.arm("dependency-only", [BASE]),
                T.arm("plus-a", [BASE, A]), T.arm("plus-b", [BASE, unrel, B])]
        res = multi_main(arms, {"baseline": m_rows, "dependency-only": [], "plus-a": m_rows[2:], "plus-b": []},
                         [A, B])
        return multi_check(res, {"000801": "not-separable", "000802": "needs-both", "000803": "single-mod"},
                           leaves_more_why(f"only {A} besides the base game", B), 1)

    # S18. The same with the "only" setups lent by a sibling (its companion form).
    def sibling_only_setup_with_companion_not_separable():
        main_ = ("main", [T.arm("baseline", [BASE, unrel, A, B, T.PATCH], kind="baseline"),
                          T.arm("without-patch", [BASE, unrel, A, B])],
                 {(C, "baseline"): [], (C, "without-patch"): m_rows}, [A, B], {"patches": [T.PATCH]})
        sib = ("eval-each", [T.arm("baseline", [BASE, unrel, A, B], kind="baseline"), T.arm("dependency-only", [BASE]),
                             T.arm("plus-a", [BASE, A]), T.arm("plus-b", [BASE, unrel, B])],
               {(C, "baseline"): m_rows, (C, "dependency-only"): [], (C, "plus-a"): m_rows[2:], (C, "plus-b"): []},
               [A, B], {"plan_id": "sibeach00000"})
        res = run_plan(main_, [sib], declare=(T.PATCH,))
        if refused(res):
            return False, refused(res)
        return multi_check(res, {"000801": "not-separable", "000802": "needs-both", "000803": "single-mod"},
                           leaves_more_why(f"only {A} besides the base game", B), 1)

    # S19. Three evaluated mods, exact "only" setups: each leaves out two, so no clash is shown to need all three.
    def three_mods_only_setups_not_separable():
        m3 = "ModC.esp"
        arms = [T.arm("baseline", [BASE, A, B, m3], kind="baseline"), T.arm("dependency-only", [BASE]),
                T.arm("plus-a", [BASE, A]), T.arm("plus-b", [BASE, B]), T.arm("plus-c", [BASE, m3])]
        res = multi_main(arms, {"baseline": m_rows, "dependency-only": [], "plus-a": m_rows[2:], "plus-b": [],
                                "plus-c": []}, [A, B, m3])
        got, md = attributions(res), res["md"] or ""
        want = {"000801": "not-separable", "000802": "not-separable", "000803": "single-mod"}
        why_b, why_c = (leaves_more_why(f"only {A} besides the base game", B),
                        leaves_more_why(f"only {A} besides the base game", m3))
        ok = got == want and why_b in md and why_c in md
        return ok, (f"attributions {got} (want {want}); why for {B} on the page: {why_b in md}; why for {m3}: "
                    f"{why_c in md}")

    # S20. A setup listing the mods in another case is the same setup: "only ModA" written in lower case still leaves
    # out exactly ModB, so both clashes need both mods.
    def only_setup_in_other_case_is_exact():
        arms = [T.arm("baseline", [BASE, unrel, A, B], kind="baseline"),
                T.arm("dependency-only", [BASE, unrel]),
                T.arm("plus-a", [BASE.lower(), unrel.lower(), A.lower()]), T.arm("plus-b", [BASE, unrel, B])]
        res = multi_main(arms, {"baseline": m_rows, "dependency-only": [], "plus-a": m_rows[2:], "plus-b": []},
                         [A, B])
        return multi_check(res, {"000801": "needs-both", "000802": "needs-both", "000803": "single-mod"})

    # S21. No input reaches it (an "only" setup always lacks the other mods), so the compiler is loaded with its
    # "only ModA" role pointed at a setup that still holds ModB: the row is not-separable with the plain sentence, and
    # the report is still generated (no StopIteration).
    def only_setup_holding_the_other_mod_not_separable():
        import importlib.util
        src_ = Path(T.R.__file__).read_text(encoding="utf-8")
        line_ = "    s_arms = {t: arm_without(pt_lower - {str(t).lower()}) for t in p_targets} if len(p_targets) > 1 else {}\n"
        if src_.count(line_) != 1:
            return False, f"the 'only' setups line occurs {src_.count(line_)} times in evalreport.py (want 1)"
        src_ = src_.replace(line_, line_ + "    if 'odd' in orders and p_targets:\n        s_arms[p_targets[0]] = 'odd'\n")
        spec_ = importlib.util.spec_from_loader("evalreport_odd_only", loader=None)
        mod_ = importlib.util.module_from_spec(spec_)
        mod_.__file__ = T.R.__file__
        exec(compile(src_, T.R.__file__, "exec"), mod_.__dict__)
        arms = [T.arm("baseline", [BASE, A, B], kind="baseline"), T.arm("dependency-only", [BASE]),
                T.arm("plus-a", [BASE, A]), T.arm("plus-b", [BASE, B]), T.arm("odd", [BASE, A, B, unrel])]
        real_ = T.R
        T.R = mod_
        try:
            res = multi_main(arms, {"baseline": m_rows[:1], "dependency-only": [], "plus-a": [], "plus-b": [],
                                    "odd": []}, [A, B])
        finally:
            T.R = real_
        why_ = T.R.bind((words().get("notSeparableWhy") or {}).get("noSetupLeavesOut") or "\0", {"other": B})
        return multi_check(res, {"000801": "not-separable"}, why_, 1)

    # G2 (a guard, green before and after). Exact "only" setups for two mods: "needs both", as before.
    def exact_only_setups_needs_both():
        arms = [T.arm("baseline", [BASE, unrel, A, B], kind="baseline"), T.arm("dependency-only", [BASE, unrel]),
                T.arm("plus-a", [BASE, unrel, A]), T.arm("plus-b", [BASE, unrel, B])]
        res = multi_main(arms, {"baseline": m_rows, "dependency-only": [], "plus-a": m_rows[2:], "plus-b": []},
                         [A, B])
        return multi_check(res, {"000801": "needs-both", "000802": "needs-both", "000803": "single-mod"})

    # G3, G4 (guards). One evaluated mod: the setup without the other mod found only with its companion is item 21's
    # fewer-plugins case, from this bundle and from a sibling: needs-some unless the other mod owns the partner object.
    def one_mod_companion_setup_needs_some():
        arms = [T.arm("baseline", [BASE, unrel, A, B], kind="baseline"), T.arm("without-b", [BASE, A]),
                T.arm("only-b", [BASE, unrel, B])]
        res = multi_main(arms, {"baseline": m_rows, "without-b": m_rows[2:], "only-b": []}, [A])
        return multi_check(res, {"000801": "needs-some", "000802": "needs-both", "000803": "single-mod"})

    def one_mod_sibling_companion_setup_needs_some():
        rows_ = [T.row("CONTACT", NB1, A, f"000801:{A}", other="000901:Skyrim.esm"),
                 T.row("CONTACT", NB1, A, f"000802:{A}", other=NB_OBJ),
                 T.row("CONTACT", NB1, A, f"000803:{A}", other="000903:Skyrim.esm")]
        order = [BASE, unrel, NB1, A]
        main_ = ("main", [T.arm("baseline", order, kind="baseline"), T.arm("patched", order + [PATCH])],
                 {(C, "baseline"): rows_, (C, "patched"): []}, [A], {"patches": [PATCH]})
        sib = ("eval-split", [T.arm("baseline", order, kind="baseline"), T.arm("without-nb", [BASE, A]),
                              T.arm("only-nb", [BASE, unrel, NB1])],
               {(C, "baseline"): rows_, (C, "without-nb"): rows_[2:], (C, "only-nb"): []}, [A],
               {"plan_id": "sibsplit0000"})
        res = run_plan(main_, [sib])
        if refused(res):
            return False, refused(res)
        return multi_check(res, {"000801": "needs-some", "000802": "needs-both", "000803": "single-mod"})

    # G1 (a guard, green before and after). One sibling through the command line behaves as it did: the plan records
    # it as `sibling` and records no list of siblings.
    def one_sibling_as_before():
        res = run_plan(main_spec(BLOCK), [combined_spec(BLOCK)])
        if refused(res):
            return False, refused(res)
        p = prediction(res)
        s_ = p.get("sibling") or {}
        got = attributions(res)
        ok = ("siblings" not in p and s_.get("anchorArm") == "baseline"
              and s_.get("roles") == {"without the evaluated mods": "neighbours-only"}
              and got == {"000801": "with-target", "000802": "with-target"})
        return ok, f"sibling roles {s_.get('roles')}; siblings key present: {'siblings' in p}; attributions {got}"

    for name, fn in [("siblings/S1-second-sibling-fills-the-partner-role", second_sibling_fills_the_partner_role),
                     ("siblings/S2-sibling-order-does-not-change-the-report", sibling_order_does_not_change_the_report),
                     ("siblings/S3-second-sibling-fills-the-setup-without-the-partner",
                      second_sibling_fills_the_setup_without_the_partner),
                     ("siblings/S4-second-sibling-fills-each-mod-alone", second_sibling_fills_each_mod_alone),
                     ("siblings/S5-report-names-the-evaluation-each-setup-came-from",
                      report_names_the_evaluation_each_setup_came_from),
                     ("siblings/S6-foreign-second-sibling-refused-by-name", foreign_second_sibling_refused_by_name),
                     ("siblings/S7-no-sibling-holds-the-shared-setup-refused", no_sibling_holds_the_shared_setup_refused),
                     ("siblings/S8-same-sibling-twice-refused", same_sibling_twice_refused),
                     ("siblings/S9-two-siblings-offer-one-setup-first-given-fills-it",
                      two_siblings_offer_one_setup_first_given_fills_it),
                     ("siblings/S10-two-siblings-measure-one-setup-differently-not-separable",
                      two_siblings_measure_one_setup_differently_not_separable),
                     ("siblings/S11-sibling-holding-the-shared-setup-takes-precedence",
                      sibling_holding_the_shared_setup_takes_precedence),
                     ("siblings/S12-kept-plugin-other-bytes-not-used", kept_plugin_other_bytes_not_used),
                     ("siblings/S13-scope-not-covering-refused", scope_not_covering_refused),
                     ("siblings/S14-one-check-build-not-comparable-others-joined",
                      one_check_build_not_comparable_others_joined),
                     ("siblings/S15-fewer-setup-absence-needs-some-unless-partner-owns",
                      fewer_setup_absence_needs_some_unless_partner_owns),
                     ("siblings/S16-sub-order-check-needs-a-shared-setup-join", sub_order_check_needs_a_shared_setup_join),
                     ("siblings/S17-only-setup-with-companion-not-separable", only_setup_with_companion_not_separable),
                     ("siblings/S18-sibling-only-setup-with-companion-not-separable",
                      sibling_only_setup_with_companion_not_separable),
                     ("siblings/S19-three-mods-only-setups-not-separable", three_mods_only_setups_not_separable),
                     ("siblings/S20-only-setup-in-other-case-is-exact", only_setup_in_other_case_is_exact),
                     ("siblings/S21-only-setup-holding-the-other-mod-not-separable",
                      only_setup_holding_the_other_mod_not_separable),
                     ("siblings/G1-one-sibling-as-before", one_sibling_as_before),
                     ("siblings/G2-exact-only-setups-needs-both", exact_only_setups_needs_both),
                     ("siblings/G3-one-mod-companion-setup-needs-some", one_mod_companion_setup_needs_some),
                     ("siblings/G4-one-mod-sibling-companion-setup-needs-some",
                      one_mod_sibling_companion_setup_needs_some)]:
        cell(name, fn)

    for name, ok, detail in cells:
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    failed = sum(1 for _, ok, _ in cells if not ok)
    print(f"\n{len(cells) - failed}/{len(cells)} cells passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
