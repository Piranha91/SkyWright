"""Known answers: a geometry row that holds in one order of a free pair and not in the other is said as a load-order risk
(C1770-C1779).

The bundle carries an `order-geometry` enrichment per probe: the rows of the setup without the patch measured in both
orders of the evaluated pair (ordersweep's measured order and its reversal), each row's physical state in each order.

    C1770  a contact row that holds in the measured order and is gone in the reversed one: one order-geometry line,
           direction "measured", naming the object and both mods, on the plan, in report.md and in report.html
    C1771  a row gone in the measured order that holds in the reversed one: one line, direction "reversed"
    C1772  a row that holds in both orders is no order risk: no line (must not fire; passes on the base by design)
    C1773  a row the reversed order could not measure is no risk line; it is counted as could-not-be-judged
    C1774  a plugin whose bytes differ from the bundle's (measured today in both orders) is named on the fact and the page
    C1775  orders that do not hold the same plugins with the same bytes are refused (order-geometry-foreign)
    C1776  two orders that are the same order are refused (order-geometry-same-order)
    C1777  each line names the enrichment file it rests on, and the evidence index lists that file
    C1778  each probe gets its own lines and counts (contact and navmesh together), and the reader's page says the count
    C1779  bundleenrich.order_geometry_rows derives each row's state in both orders from ordersweep's three emits

Usage:
  python _geometry-harness/tests/test_order_geometry.py
Exit 0 all pass, 1 a cell failed.
"""
from __future__ import annotations

import json
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))  # the module's folder
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
sys.path.append(str(SW.HARNESS / "tests"))  # SkyWright's own suites and fixtures the module's suites reuse
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
import test_evalreport_prediction as TP                               # noqa: E402
import evalreport as R                                                # noqa: E402

CONTACT, NAVOWN, BASE = TP.CONTACT, TP.NAVOWN, TP.BASE
MOD_A, MOD_B, OTHER = TP.MOD_A, TP.MOD_B, TP.OTHER
DRIFT = "Curios.esl"
MEASURED = [BASE, DRIFT, OTHER, MOD_A, MOD_B]
REVERSED = [BASE, DRIFT, OTHER, MOD_B, MOD_A]
PROBE_OF = {CONTACT: "contactprobe", NAVOWN: "navownprobe"}


def geo_row(form_key, measured, reversed_, kind="CONTACT", label=None, a=MOD_A, b=MOD_B, other=None):
    return {"Kind": kind, "A": a, "B": b, "FormKey": form_key, "Other": other, "Label": label or f"object {form_key[:6]}",
            "measured": measured, "reversed": reversed_}


def geo_doc(check_id, rows, measured=MEASURED, reversed_=REVERSED, shas=None, drifted=()):
    shas = dict(shas or {})
    return {"probe": PROBE_OF[check_id], "checkId": check_id, "pair": [MOD_A, MOD_B], "rule": "free (both non-master)",
            "orders": {"measured": [{"name": n, "sha256": shas.get(("m", n), shas.get(n, "0" * 64))} for n in measured],
                       "reversed": [{"name": n, "sha256": shas.get(("r", n), shas.get(n, "0" * 64))} for n in reversed_]},
            "drifted": [{"name": n, "bundle": "0" * 64, "measured": "9" * 64} for n in drifted],
            "rows": rows, "derivedFrom": [{"path": f"sweep/{PROBE_OF[check_id]}/baseline.json", "sha256": "1" * 64}]}


def compile_geo(docs, want_index=False):
    """The real compiler and templates on a two-mod bundle carrying one order-geometry enrichment per doc."""
    arms = [TP.arm("baseline", MEASURED, kind="baseline")]
    results = {(CONTACT, "baseline"): [], (NAVOWN, "baseline"): []}
    pr = json.loads(TP.PRIORITIES.read_text(encoding="utf-8"))
    conc, page = pr.get("concise") or {}, pr.get("page") or {}
    settings = {"mode": "concise", "layout": conc.get("layout", "normal"), "maxPages": conc.get("maxPages", 8),
                "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16),
                         "baseFontPt": page.get("baseFontPt", 10.5)},
                "declaredPatches": [], "rowsPerGroup": conc.get("rowsPerGroup", 3),
                "figuresPerFinding": conc.get("figuresPerFinding", 1),
                "reel": {k: (pr.get("reel") or {}).get(k) for k in ("maxSeconds", "fps", "width", "height")}}
    with tempfile.TemporaryDirectory() as td:
        bdir = TP.make_bundle(Path(td) / "bundle", arms, results, [MOD_A, MOD_B])
        man = json.loads((bdir / "manifest.json").read_text(encoding="utf-8"))
        for n, doc in enumerate(docs):
            rel = f"enrichment/order-geometry/{doc['checkId']}.json"
            (bdir / rel).parent.mkdir(parents=True, exist_ok=True)
            (bdir / rel).write_text(json.dumps(doc), encoding="utf-8")
            man["evidence"].append({"id": f"g{n:015x}", "kind": "enrichment", "enrichment": "order-geometry",
                                    "path": rel, "sha256": "6" * 64, "checkId": doc["checkId"], "armId": "baseline",
                                    "recordedBy": "enrichment:order-geometry", "derivedFrom": doc["derivedFrom"]})
        (bdir / "manifest.json").write_text(json.dumps(man), encoding="utf-8")
        plan = R.compile_plan(bdir, {"bundle": {"complete": True}}, settings, pr, str(bdir))
        view = R.effective_view(plan, settings["rowsPerGroup"], settings["figuresPerFinding"], pr, "normal")
        out = R.render_all(plan, view, Path(td) / "out", "")
    if want_index:
        return plan, out["report.md"], out["report.html"], out["evidence-index.md"]
    return plan, out["report.md"], out["report.html"]


def geo_lines(plan):
    return list((TP.reader(plan).get("prediction") or {}).get("orderGeometryLines") or [])


def geo_fact(plan):
    return ((TP.reader(plan).get("prediction") or {}).get("loadOrder") or {}).get("geometry") or {}


def refusal(docs):
    try:
        compile_geo(docs)
    except R.InputRefused as ex:
        return str(ex)
    return ""


def main() -> int:
    cells = []

    def cell(name, fn):
        try:
            ok, detail = fn()
        except Exception:                                            # noqa: BLE001
            ok, detail = False, "raised: " + traceback.format_exc().strip().splitlines()[-1]
        cells.append((name, ok, detail))

    def c1770_holds_measured_only():
        plan, md, html = compile_geo([geo_doc(CONTACT, [geo_row(f"000A01:{BASE}", "Holds", "Gone", label="Bench")])])
        ls = geo_lines(plan)
        t = ls[0]["text"] if ls else ""
        ok = (len(ls) == 1 and ls[0].get("direction") == "measured" and ls[0].get("probe") == "contactprobe"
              and "Bench" in t and MOD_A in t and MOD_B in t and t in md and "Bench" in html)
        return ok, f"lines {ls}"

    def c1771_holds_reversed_only():
        plan, md, html = compile_geo([geo_doc(CONTACT, [geo_row(f"000A02:{BASE}", "Gone", "Holds", label="Barrel")])])
        ls = geo_lines(plan)
        ok = len(ls) == 1 and ls[0].get("direction") == "reversed" and "Barrel" in ls[0]["text"] and ls[0]["text"] in md
        return ok, f"lines {ls}"

    def c1772_both_orders_no_line():
        plan, md, html = compile_geo([geo_doc(CONTACT, [geo_row(f"000A03:{BASE}", "Holds", "Holds", label="Crate")])])
        ls = geo_lines(plan)
        return (not ls and "Crate" not in md), f"lines {ls}"

    def c1773_unmeasured_counted():
        plan, md, html = compile_geo([geo_doc(CONTACT, [geo_row(f"000A04:{BASE}", "Holds", "Unmeasured", label="Sack")])])
        ls, f = geo_lines(plan), geo_fact(plan)
        ok = not ls and f.get("measured") is True and f.get("unjudged") == 1
        return ok, f"lines {ls}; fact {f}"

    def c1774_drift_named():
        plan, md, html = compile_geo([geo_doc(CONTACT, [geo_row(f"000A05:{BASE}", "Holds", "Gone")], drifted=[DRIFT])])
        f = geo_fact(plan)
        ok = f.get("drifted") == [DRIFT] and DRIFT in md
        return ok, f"fact {f}"

    def c1775_foreign_refused():
        r1 = refusal([geo_doc(CONTACT, [], reversed_=[BASE, DRIFT, OTHER, MOD_B, MOD_A, "Extra.esp"])])
        r2 = refusal([geo_doc(CONTACT, [], shas={("r", MOD_A): "a" * 64})])
        ok = r1.startswith("order-geometry-foreign") and r2.startswith("order-geometry-foreign")
        return ok, f"{r1[:70]!r}; {r2[:70]!r}"

    def c1776_same_order_refused():
        r_ = refusal([geo_doc(CONTACT, [], reversed_=MEASURED)])
        return r_.startswith("order-geometry-same-order"), f"{r_[:80]!r}"

    def c1777_index_lists_file():
        plan, md, html, index = compile_geo([geo_doc(CONTACT, [geo_row(f"000A06:{BASE}", "Holds", "Gone")])],
                                            want_index=True)
        rel = "enrichment/order-geometry/geometry.placement-contact.json"
        ls = geo_lines(plan)
        ok = len(ls) == 1 and ls[0].get("path") == rel and rel in index
        return ok, f"line paths {[x.get('path') for x in ls]}; index has the file {rel in index}"

    def c1778_per_probe():
        plan, md, html = compile_geo([
            geo_doc(CONTACT, [geo_row(f"000A07:{BASE}", "Holds", "Gone"), geo_row(f"000A08:{BASE}", "Gone", "Holds")]),
            geo_doc(NAVOWN, [geo_row(f"000B01:{BASE}", "Holds", "Gone", kind="EDGE-IDX", other=f"000B02:{BASE}")])])
        ls, f = geo_lines(plan), geo_fact(plan)
        by = f.get("byProbe") or {}
        import readerdoc as RD
        rd = json.loads(TP.PRIORITIES.read_text(encoding="utf-8"))["readerDoc"]
        page = RD.build(plan, rd, {MOD_A: "Mod A", MOD_B: "Mod B"}, None, None, None, "evidence-index.html")[0]
        want_page = rd["order"]["geometry"].replace("{count}", "3")
        ok = (want_page in page and sorted((x.get("probe"), x.get("direction")) for x in ls)
              == [("contactprobe", "measured"), ("contactprobe", "reversed"), ("navownprobe", "measured")]
              and (by.get("contactprobe") or {}).get("measuredOnly") == 1
              and (by.get("contactprobe") or {}).get("reversedOnly") == 1
              and (by.get("navownprobe") or {}).get("measuredOnly") == 1)
        return ok, f"lines {[(x.get('probe'), x.get('direction')) for x in ls]}; byProbe {by}"

    def c1779_rows_from_ordersweep_emits():
        import bundleenrich as BE
        with tempfile.TemporaryDirectory() as td:
            d = Path(td)

            def conflict(fk, other=None, phys="Holds"):
                return {"Kind": "CONTACT", "A": MOD_A, "B": MOD_B, "Summary": "s",
                        "Necessary": [{"FormKey": fk, "Label": f"obj {fk[:6]}", "Physical": phys}],
                        **({"Alternative": [{"FormKey": other, "Role": "placed-ref"}]} if other else {})}

            def pred(fk, phys, other=None):
                return {"Kind": "CONTACT", "A": MOD_A, "B": MOD_B, "FormKey": fk, "Other": other, "Physical": phys}
            k1, k2, k3, k4 = (f"00000{i}:{BASE}" for i in (1, 2, 3, 4))
            # measured order: k1 and k2 hold; the reversed order re-measures them: k1 gone, k2 holds
            (d / "base.json").write_text(json.dumps({"Conflicts": [conflict(k1), conflict(k2)]}), encoding="utf-8")
            # reversed order: k2 and k3 and k4 hold; the measured order re-measures k3 gone, k4 unmeasured (k2 holds)
            (d / "cand.json").write_text(json.dumps({"Conflicts": [conflict(k2), conflict(k3), conflict(k4)],
                                                     "Predicates": [pred(k1, "Gone"), pred(k2, "Holds")]}),
                                         encoding="utf-8")
            (d / "rev.json").write_text(json.dumps({"Conflicts": [conflict(k1), conflict(k2)],
                                                    "Predicates": [pred(k2, "Holds"), pred(k3, "Gone"),
                                                                   pred(k4, "Unmeasured")]}), encoding="utf-8")
            rows = BE.order_geometry_rows(d / "base.json", d / "cand.json", d / "rev.json")
        got = {r["FormKey"]: (r["measured"], r["reversed"]) for r in rows}
        want = {k1: ("Holds", "Gone"), k2: ("Holds", "Holds"), k3: ("Gone", "Holds"), k4: ("Unmeasured", "Holds")}
        return got == want, f"got {got}"

    for name, fn in [("order-geometry/C1770-holds-in-measured-order-only", c1770_holds_measured_only),
                     ("order-geometry/C1771-holds-in-reversed-order-only", c1771_holds_reversed_only),
                     ("order-geometry/C1772-holds-in-both-no-line", c1772_both_orders_no_line),
                     ("order-geometry/C1773-unmeasured-counted-not-said", c1773_unmeasured_counted),
                     ("order-geometry/C1774-drifted-plugin-named", c1774_drift_named),
                     ("order-geometry/C1775-foreign-orders-refused", c1775_foreign_refused),
                     ("order-geometry/C1776-same-order-refused", c1776_same_order_refused),
                     ("order-geometry/C1777-index-lists-the-file", c1777_index_lists_file),
                     ("order-geometry/C1778-lines-and-counts-per-probe", c1778_per_probe),
                     ("order-geometry/C1779-rows-from-ordersweep-emits", c1779_rows_from_ordersweep_emits)]:
        cell(name, fn)
    for name, ok, detail in cells:
        print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
    failed = sum(1 for _, ok, _ in cells if not ok)
    print(f"\n{len(cells) - failed}/{len(cells)} cells passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
