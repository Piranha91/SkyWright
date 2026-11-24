"""readerdoc - the reader's document: one or two pages in ordinary words, rendered from a presentation plan.

  python _geometry-harness/readerdoc.py --presentation OUT/presentation.json --out DIR
      [--mod-name PLUGIN=NAME ...] [--names NAMES.json] [--order ORDER.json]
      [--single-mod show|aside] [--headline-unit objects|relationships] [--index-link PATH] [--no-print]

The plan (`evalreport plan`) owns every join and count; this renders it. Only prediction items the plan attributes
to both evaluated mods together (`needs-both`) are listed as problems. Clashes one evaluated mod has with a third
mod on its own are shown under their own heading (or set aside with --single-mod aside); every other result stays
in the evidence index and is counted in one sentence. All wording is the reporting skill's `readerDoc` block.

Names come from the plan (`reader.names`, which the compiler copies from the bundle's names enrichment) unless
overridden; the manifest says which (`namesSource`) and where each mod's name came from (`modNameSources`).
The object and relationship counts are the compiler's (`items[].objects`, `items[].relationships`); a plan without
them, or whose object count disagrees with its own rows, is refused. The single-mod mode defaults to the plan's.

Optional inputs, each recorded with its sha256 in reader-manifest.json:
  --mod-name PLUGIN=NAME  the mod's ordinary name, overriding the plan's (default without either: the plugin name
                          without its extension)
  --names FILE            {"places": {editorId: name}, "objects": {editorId: name}, "source": ...}, overriding the
                          plan's names; a place neither names is counted, never printed by its editor name, an object
                          gets its kind word, and both are listed under `namesMissing`
  --order FILE            {"forced": true|false, "first": PLUGIN}; absent, the page says order was not checked

Writes reader.md, reader.html, reader.pdf (unless --no-print), summary.txt (the short answer, for a session
summary) and reader-manifest.json. Exit 0 written; 2 a refused input, named in one sentence on stderr.
One Run per invocation (scripts/runlog.py), no child process except the headless browser for the page count.
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import html as htmlmod
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
import evalreport as ER  # noqa: E402  (imports scripts/runlog.py for us)
from runlog import Run  # noqa: E402

EXIT_REFUSED = 2
SUMMARY_RE = re.compile(r"^\w+: (\w+) (\S+)")
CAUSE_RE = re.compile(r"which (.+?) (Moved|Removed);")


def sha(p: Path) -> str:
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def bind(t: str, **kw) -> str:
    import reel_plan
    return reel_plan.fill(t, **kw)


def plural(pair, n):
    return pair[0] if n == 1 else pair[1]


def join_words(xs):
    xs = [x for x in xs if x]
    if len(xs) <= 1:
        return "".join(xs)
    return ", ".join(xs[:-1]) + " and " + xs[-1]


def clean_place(label: str) -> str:
    return re.sub(r"\s*\((int|ext)\)\s*$", "", str(label or "")).strip()


class Refused(Exception):
    pass


def item_key(e):
    return (e["checkId"], e["kind"], tuple(sorted([e["A"], e["B"]], key=str.lower)), e.get("attribution"),
            e.get("attributionMod"))


def plan_names(reader: dict) -> tuple[dict | None, dict]:
    """The plan's names in the shape the page reads ({places: {id: name}, objects: {id: name}}) and its mod names."""
    pn = reader.get("names")
    if not isinstance(pn, dict):
        return None, {}
    flat = {k: {i: (v.get("name") if isinstance(v, dict) else v) for i, v in (pn.get(k) or {}).items()
                if (v.get("name") if isinstance(v, dict) else v)} for k in ("places", "objects")}
    return flat, {m: v for m, v in (pn.get("mods") or {}).items() if isinstance(v, dict) and v.get("name")}


def build(plan: dict, rd: dict, mod_names: dict, names: dict | None, order: dict | None, single_mod: str | None,
          index_link: str | None, unit: str | None = None) -> tuple[str, str, dict]:
    reader = plan.get("reader") or {}
    pred = reader.get("prediction")
    val = reader.get("validation") or {}
    if not pred:
        raise Refused("the plan has no prediction block; compile it with the current evalreport plan")
    mods = list(pred.get("mods") or [])
    if len(mods) != 2:
        raise Refused(f"the reader's document needs exactly two evaluated mods; the plan names {len(mods)}")
    if any(not isinstance(it.get("objects"), int) or not isinstance(it.get("relationships"), int)
           for it in pred.get("items") or []):
        raise Refused("the plan predates per-item object and relationship counts; recompile it with evalreport plan")
    single_mod = single_mod or pred.get("singleModMode") or "show"
    unit = unit or rd.get("headlineUnit") or "objects"
    p_names, p_mods = plan_names(reader)
    names_source = "override" if names is not None else ("plan" if p_names is not None else "none")
    if names is None:
        names = p_names
    disp, mod_sources = {}, {}
    for m in mods:
        if mod_names.get(m):
            disp[m], mod_sources[m] = mod_names[m], "override"
        elif m in p_mods:
            disp[m], mod_sources[m] = p_mods[m]["name"], p_mods[m].get("source") or "plan"
        else:
            disp[m], mod_sources[m] = re.sub(r"\.(esp|esm|esl)$", "", m, flags=re.I), "plugin-name"
    mod_a, mod_b = disp[mods[0]], disp[mods[1]]
    has_patch = val.get("status") == "compared"
    if has_patch and not isinstance(val.get("byItem"), list):
        raise Refused("the plan predates per-item patch counts (validation.byItem); recompile it with evalreport plan")
    findings = {f["id"]: f for f in plan.get("findings") or []}
    record = pred.get("record") or []
    places_map = (names or {}).get("places") or {}
    objects_map = (names or {}).get("objects") or {}
    missing_names = {"places": set(), "objects": set()}
    units_t, phrases, kind_words = rd["units"], rd["problemPhrases"], rd["kindWords"]

    def describe(i, it, limit=None):
        k = (it["checkId"], it["kind"], tuple(it["mods"]), it["attribution"], it.get("attributionMod"))
        keys = {(e["formKey"], e["summary"]) for e in record if e.get("admission") == "admitted" and item_key(e) == k}
        rows = [r for fid in it.get("findingIds") or [] for r in (findings.get(fid) or {}).get("allRows") or []
                if (r.get("formKey"), r.get("summary")) in keys]
        subjects = collections.OrderedDict()
        for r in rows:
            subjects.setdefault(r["formKey"], r)
        n = len(subjects) or int(it.get("objects") or 0)
        noun = ((rd.get("relationshipUnits") or {}).get(it["checkId"]) or (rd.get("relationshipUnits") or {}).get("default")
                if unit == "relationships" else None) or units_t.get(it["checkId"]) or units_t["default"]
        per_place = collections.Counter()
        unnamed = set()
        objects = collections.Counter()
        causes = collections.Counter()
        for fk, r in subjects.items():
            pl = clean_place(r.get("label"))
            if pl:
                if pl in places_map:
                    per_place[places_map[pl]] += 1
                else:
                    missing_names["places"].add(pl)
                    unnamed.add(pl)
            m = SUMMARY_RE.match(str(r.get("summary") or ""))
            if m:
                kind, eid = m.group(1), m.group(2)
                if eid in objects_map:
                    nm = objects_map[eid].lower()
                    objects[(nm, nm + "s")] += 1
                else:
                    missing_names["objects"].add(eid)
                    objects[tuple(kind_words.get(kind) or kind_words["default"])] += 1
        if len(subjects) and int(it.get("objects")) != len(subjects):
            raise Refused(f"the plan counts {it.get('objects')} objects for {it['checkId']} but its rows name "
                          f"{len(subjects)}; recompile it with evalreport plan")
        if unit == "relationships":
            n = int(it.get("relationships"))
        for fk, r in subjects.items():
            whos = {mm.group(1) for rr in rows if rr["formKey"] == fk
                    for mm in [CAUSE_RE.search(str(rr.get("summary") or ""))] if mm}
            for who in whos:
                causes[who] += 1
        phrase = bind(phrases.get(it["checkId"]) or phrases["default"], n=ER.fmt_n(n), units=plural(noun, n),
                      title=it.get("title") or it["checkId"], verb="is" if n == 1 else "are",
                      they="it" if n == 1 else "they", show="shows" if n == 1 else "show")
        # a place with no in-game name is counted, never printed by its editor name; a list may be capped
        shown_pl = per_place.most_common(limit) if limit else per_place.most_common()
        parts = [f"{p} ({c})" if len(per_place) > 1 else p for p, c in shown_pl]
        more = len(per_place) - len(shown_pl)
        if more:
            parts.append(bind(rd["placesMore"], n=ER.fmt_n(more), places=plural(rd["placeWord"], more)))
        if unnamed:
            parts.append(bind(rd["placesUnnamed"], n=ER.fmt_n(len(unnamed)), places=plural(rd["placeWord"], len(unnamed))))
        places_txt = join_words(parts)
        obj_txt = join_words([f"{ER.fmt_n(c)} {plural(w, c)}" for w, c in objects.most_common()])
        return {"index": i, "phrase": phrase, "n": n, "unit": noun, "places": places_txt, "perPlace": dict(per_place),
                "objects": obj_txt, "causes": dict(causes), "rows": len(rows), "distinct": len(subjects),
                "planCount": it.get("count")}

    items = pred.get("items") or []
    both = [(i, it) for i, it in enumerate(items) if it.get("attribution") == "needs-both"]
    alone = [(i, it) for i, it in enumerate(items) if it.get("attribution") == "single-mod"]
    other = [(i, it) for i, it in enumerate(items) if it.get("attribution") not in ("needs-both", "single-mod")]
    d_both = [describe(i, it) for i, it in both]
    d_alone = [describe(i, it, rd.get("aloneMaxPlaces") or 3) for i, it in alone]

    title = bind(rd["title"] if has_patch else rd["titleNoPatch"], modA=mod_a, modB=mod_b)
    h = rd["headings"]
    md = [f"# {title}", ""]
    # the short answer
    if d_both:
        summary = join_words([d["phrase"] for d in d_both])
        opening = bind(rd["opening"] if has_patch else rd["openingNoPatch"], modA=mod_a, modB=mod_b, summary=summary)
        places = join_words(list(dict.fromkeys(p for d in d_both for p in d["perPlace"])))
        answer = [opening] + ([bind(rd["where"], places=places)] if places else [])
    else:
        answer = [bind(rd["openingNone"], modA=mod_a, modB=mod_b)]
    ins = rd["install"]
    if not has_patch:
        install = ins["noPatch"]
    elif int(val.get("patchProblems") or 0) > 0:
        install = bind(ins["patchProblems"], count=ER.fmt_n(val["patchProblems"]),
                       noun="problem" if int(val["patchProblems"]) == 1 else "problems")
    else:
        # A lost record field the patch sets is its choice, not a fix (which value is right is taste): the install
        # rule weighs only the problems a patch can fix; the record fields get their own sentence.
        by = val["byItem"]
        fixable = [(i, it) for i, it in both if it.get("kind") != "LOST-EDIT"]
        hits = sum(by[i]["hits"] for i, _ in fixable)
        total = sum(int(it.get("count") or 0) for _, it in fixable)
        left = sum(by[i]["open"] + by[i]["notMeasured"] + by[i].get("cannotTell", 0) for i, _ in fixable)
        if fixable and hits == total and left == 0:
            install = ins["all"]
        elif not fixable:
            install = ins["noPatch"] if not has_patch else bind(ins["some"], hits=0, total=0, left=0)
        else:
            install = bind(ins["some"], hits=ER.fmt_n(hits), total=ER.fmt_n(total), left=ER.fmt_n(left))
    answer.append(install)
    md += [f"## {h['answer']}", ""] + [f"> {s}" for s in answer] + [""]
    # the problems
    if d_both:
        md += [f"## {h['problems'] if has_patch else h['problemsNoPatch']}", ""]
        for d in d_both:
            line = f"- **{d['phrase'][0].upper() + d['phrase'][1:]}** when {mod_a} and {mod_b} are used together."
            if d["places"]:
                line += f" Where: {d['places']}."
            if d["objects"]:
                line += " " + bind(rd["objectsLine"], objects=d["objects"])
            for who, c in sorted(d["causes"].items(), key=lambda x: -x[1]):
                line += " " + bind(rd["causeLine"], mod=disp.get(who, re.sub(r"\.(esp|esm|esl)$", "", who)),
                                   count=ER.fmt_n(c))
            md.append(line)
        md.append("")
    if d_alone and single_mod == "show":
        md += [f"## {h['alone']}", ""]
        for (i, it), d in zip(alone, d_alone):
            am = it.get("attributionMod")
            oth = [m for m in it["mods"] if m != am]
            md.append("- " + bind(rd["aloneLine"], n=ER.fmt_n(d["n"]), units=plural(d["unit"], d["n"]),
                                  mod=disp.get(am, re.sub(r"\.(esp|esm|esl)$", "", str(am))),
                                  other=re.sub(r"\.(esp|esm|esl)$", "", oth[0] if oth else "another mod"))
                      + (f" Where: {d['places']}." if d["places"] else ""))
        md.append("")
    # the patch
    if has_patch:
        md += [f"## {h['patch']}", ""]
        sents = []
        if d_both:
            fixed = [bind(rd["patchFixesItem"], n=ER.fmt_n(d["n"]), units=plural(d["unit"], d["n"]))
                     for (i, it), d in zip(both, d_both) if it.get("kind") != "LOST-EDIT"
                     and val["byItem"][i]["open"] == 0 and val["byItem"][i]["notMeasured"] == 0
                     and val["byItem"][i].get("cannotTell", 0) == 0]
            if fixed:
                sents.append(bind(rd["patchFixes"], phrase=join_words(fixed)))
            rec = [(i, it) for i, it in both if it.get("kind") == "LOST-EDIT"]
            if rec:
                # a field the patch keeps on purpose is still one it sets (evalreport's four lost-edit outcomes)
                sets = sum(val["byItem"][i]["hits"] + val["byItem"][i].get("kept", 0) for i, _ in rec)
                n_rec = sum(int(it.get("count") or 0) for _, it in rec)
                sents.append(bind(rd["patchSetsFields"], sets=ER.fmt_n(sets), total=ER.fmt_n(n_rec),
                                  left=ER.fmt_n(n_rec - sets)))
        for (i, it), d in zip(alone, d_alone):
            if val["byItem"][i]["open"]:
                am = it.get("attributionMod")
                sents.append(bind(rd["patchLeaves"], n=ER.fmt_n(d["n"]), units=plural(d["unit"], d["n"]),
                                  mod=disp.get(am, str(am))))
        if int(val.get("patchProblems") or 0) == 0:
            sents.append(rd["patchNone"])
        md += [" ".join(sents), ""]
    # order
    od = rd["order"]
    lo_ = pred.get("loadOrder") if isinstance(pred.get("loadOrder"), dict) else None
    if order is None and lo_ and lo_.get("forced") is not None:
        # item 18 (4.24 phase 3): the plan's own order fact, in the page's words and each mod's ordinary name; an
        # --order side file, when given, still decides (it is recorded in the manifest)
        def nm_(x):
            return disp.get(x, str(x))
        mo_ = lo_.get("measuredOrder") or [None, None]
        ch_ = lo_.get("changeHands")
        if lo_["forced"]:
            o = bind(od["forced"] if lo_.get("rule") == "ESM flag" else od["forcedMaster"], first=nm_(lo_.get("loadsFirst")))
        elif not ch_:
            o = bind(od["free"], first=nm_(mo_[0]))
        else:
            def lost_(d_):
                return "; ".join(bind(od["lostPart"], count=ER.fmt_n(n_), plugin=nm_(x_)) for x_, n_ in d_.items()) \
                    or od["lostNone"]
            o = bind(od["freeBoth"], count=ER.fmt_n(ch_["fields"]), first=nm_(mo_[0]), second=nm_(mo_[1]),
                     measured=lost_(ch_.get("lostByMeasuredOrder") or {}), reversed=lost_(ch_.get("lostByReversed") or {}))
            wp_ = lo_.get("withPatch")
            if wp_ is not None:
                o += " " + (bind(od["withPatchMatters"], count=ER.fmt_n(wp_["changeHands"])) if wp_.get("orderMatters")
                            else od["withPatchNot"])
    elif order and order.get("forced") is True:
        rule = str(order.get("rule") or "").lower()
        o = bind(od["forced"] if "esm" in rule else od["forcedMaster"],
                 first=disp.get(order.get("first"), str(order.get("first"))))
    elif order and order.get("forced") is False:
        o = bind(od["free"], first=disp.get(order.get("first"), str(order.get("first"))))
    else:
        o = od["unknown"]
    geo_ = (lo_ or {}).get("geometry")
    if geo_ and od.get("geometry"):
        # item 10: placed-object geometry measured in both orders
        n_ = int(geo_.get("measuredOnly") or 0) + int(geo_.get("reversedOnly") or 0)
        o += " " + (bind(od["geometry"], count=ER.fmt_n(n_)) if n_ else od["geometryNone"])
    md += [f"## {h['order']}", "", o, ""]
    # how sure
    subjects = []
    for line in (reader.get("coverage") or {}).get("lines") or []:
        subj = str(line.get("subject") or "")
        while re.search(r"\([^()]*\)", subj):
            subj = re.sub(r"\s*\([^()]*\)", "", subj)
        for s in subj.split(";"):
            s = s.strip()
            s = re.sub(r":.*$", "", s).strip()
            if s and s[0].isupper() and s.lower() not in (x.lower() for x in subjects):
                subjects.append(s)
    plain_t = rd.get("plainTitles") or {}
    subjects = [plain_t.get(s, s) for s in subjects]
    shown = subjects[:6]
    lst = "; ".join([s[0].lower() + s[1:] for s in shown]
                    + ([bind(rd["sureMore"], n=len(subjects) - len(shown))] if len(subjects) > len(shown) else []))
    md += [f"## {h['sure']}", "", rd["sureFirst"]] + ([bind(rd["sureNotChecked"], list=lst)] if shown else []) + [""]
    aside_n = sum(int(v or 0) for v in (pred.get("setAside") or {}).values())
    aside_n += sum(int(it.get("count") or 0) for _, it in other)
    if single_mod == "aside":
        aside_n += sum(int(it.get("count") or 0) for _, it in alone)
    if aside_n:
        md += [bind(rd["setAside"], n=ER.fmt_n(aside_n)), ""]
    # a same-named patch by its label (its folder), never its bare name twice (item 20)
    files = list(mods) + list(val.get("patchLabels") or val.get("patches") or [])
    md += [bind(rd["filesLine"], files=", ".join(files))]
    if index_link:
        md += ["", bind(rd["techLine"], link=f"[evidence index]({index_link})")]
    text = "\n".join(md).rstrip() + "\n"
    summary_txt = "\n".join([title] + answer) + "\n"
    manifest = {"title": title, "mods": disp,
                "admitted": [{"checkId": it["checkId"], "attribution": it["attribution"], "planCount": d["planCount"],
                              "rows": d["rows"], "distinct": d["distinct"], "perPlace": d["perPlace"],
                              "causes": d["causes"], "byItem": (val.get("byItem") or [None] * len(items))[i]}
                             for (i, it), d in zip(both, d_both)],
                "singleModItems": [{"checkId": it["checkId"], "attributionMod": it.get("attributionMod"),
                                    "planCount": it.get("count"), "shown": single_mod == "show"} for i, it in alone],
                "singleModMode": single_mod, "headlineUnit": unit, "namesSource": names_source,
                "modNameSources": mod_sources,
                "otherAttribution": [{"checkId": it["checkId"], "attribution": it.get("attribution"),
                                      "planCount": it.get("count")} for i, it in other],
                "setAsideCounted": aside_n, "setAsideByReason": pred.get("setAside"),
                "namesMissing": {k: sorted(v) for k, v in missing_names.items()},
                "install": install, "notCheckedListed": len(shown), "notCheckedTotal": len(subjects)}
    return text, summary_txt, manifest


def md_to_html(md: str, css: str, title: str) -> str:
    out, in_list = [], False

    def inline(s):
        s = htmlmod.escape(s)
        s = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", s)
        return re.sub(r"\[(.+?)\]\((.+?)\)", r'<a href="\2">\1</a>', s)
    for ln in md.splitlines():
        if ln.startswith("- "):
            if not in_list:
                out.append("<ul>")
                in_list = True
            out.append(f"<li>{inline(ln[2:])}</li>")
            continue
        if in_list:
            out.append("</ul>")
            in_list = False
        if ln.startswith("# "):
            out.append(f"<h1>{inline(ln[2:])}</h1>")
        elif ln.startswith("## "):
            out.append(f"<h2>{inline(ln[3:])}</h2>")
        elif ln.startswith("> "):
            out.append(f"<blockquote><p>{inline(ln[2:])}</p></blockquote>")
        elif ln.strip():
            out.append(f"<p>{inline(ln)}</p>")
    if in_list:
        out.append("</ul>")
    return (f"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{htmlmod.escape(title)}</title>"
            f"<style>{css}</style></head><body>\n" + "\n".join(out) + "\n</body></html>\n")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--presentation", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--mod-name", action="append", default=[], help="PLUGIN=NAME, the mod's ordinary name")
    ap.add_argument("--names", help="in-game names for places and objects (JSON)")
    ap.add_argument("--order", help="forced/free load-order fact (JSON)")
    ap.add_argument("--single-mod", choices=("show", "aside"), default=None,
                    help="override the plan's single-mod mode")
    ap.add_argument("--headline-unit", choices=("objects", "relationships"), default=None,
                    help="override the reporting skill's readerDoc headlineUnit")
    ap.add_argument("--index-link", help="relative link to the technical record (the evidence index)")
    ap.add_argument("--no-print", action="store_true")
    a = ap.parse_args(argv)
    out = Path(a.out)
    with Run("readerdoc", stage_source="counted") as r:
        def refuse(msg):
            print(f"REFUSED: {msg}", file=sys.stderr)
            r.refuse(msg)
            return EXIT_REFUSED
        plan, err = ER.load_presentation(Path(a.presentation))
        if plan is None:
            return refuse(err)
        pr = ER.load_priorities()
        rd = pr.get("readerDoc")
        if not isinstance(rd, dict):
            return refuse("the reporting skill's data has no readerDoc block")
        mod_names = {}
        for x in a.mod_name:
            if "=" not in x:
                return refuse(f"--mod-name takes PLUGIN=NAME, got {x!r}")
            k, v = x.split("=", 1)
            mod_names[k.strip()] = v.strip()
        names = ER.load_json(Path(a.names)) if a.names else None
        if a.names and not isinstance(names, dict):
            return refuse(f"--names {a.names} is absent or not JSON")
        order = ER.load_json(Path(a.order)) if a.order else None
        if a.order and not isinstance(order, dict):
            return refuse(f"--order {a.order} is absent or not JSON")
        try:
            text, summary, man = build(plan, rd, mod_names, names, order, a.single_mod, a.index_link,
                                       a.headline_unit)
        except Refused as ex:
            return refuse(str(ex))
        out.mkdir(parents=True, exist_ok=True)
        ER.write_text(out / "reader.md", text)
        ER.write_text(out / "summary.txt", summary)
        css = (ER.TEMPLATES / "report.css").read_text(encoding="utf-8")
        ER.write_text(out / "reader.html", md_to_html(text, css, man["title"]))
        prn = {"status": "not-rendered", "pages": None}
        if not a.no_print:
            browser, version, why = ER.resolve_browser()
            if browser is None:
                prn["reason"] = why
            else:
                ok, why = ER.print_pdf(browser, out / "reader.html", out / "reader.pdf")
                if ok:
                    pages, root, note = ER.pdf_page_count(out / "reader.pdf")
                    prn = {"status": "rendered", "pages": pages, "rootCount": root, "note": note,
                           "browser": version}
                else:
                    prn["reason"] = why
        man.update({"inputs": {"presentation": {"path": str(a.presentation), "sha256": sha(Path(a.presentation))},
                               "names": {"path": a.names, "sha256": sha(Path(a.names))} if a.names else None,
                               "order": {"path": a.order, "sha256": sha(Path(a.order))} if a.order else None,
                               "modNames": mod_names},
                    "generator": {"readerdoc.py": sha(Path(__file__)), "report-priorities.json": sha(ER.PRIORITIES),
                                  "report.css": sha(ER.TEMPLATES / "report.css")},
                    "print": prn})
        ER.write_json(out / "reader-manifest.json", man)
        r.feature("admitted", len(man["admitted"]), "items")
        r.feature("pages", prn.get("pages") or 0, "pages")
        print(json.dumps({"out": str(out), "pages": prn.get("pages"), "admitted": len(man["admitted"]),
                          "namesMissing": man["namesMissing"]}))
        return 0


if __name__ == "__main__":
    sys.exit(main())
