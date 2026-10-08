"""Miss diagnosis over a patch scorecard's unjudged edits (patch-miss packet, 2026-09-28).

Input: an edits CSV with the columns group, formKey, kind, category (the 4.24 scorecard's edits.csv shape), plus per-record
bases, cells and probe-trace outcomes gathered by the caller. Output: clusters, one miss reason per cluster, and the miss
table (reason x edit count, split by evidence type and by direct vs inherited counts).

The API and the rules were frozen at Gate B (GATE-A §4 and §12, GATE-B §2); tests/test_missdiag.py is their known-answer
suite. Command line: `python missdiag.py INPUT.json OUTPUT.json` (see run()); each run is recorded through
scripts/runlog.py.

Frozen rules, in brief:
- record action: a record's category-5 spatial kinds, sorted and joined with '+'.
- cluster key: (group, action, base, cell, trace signature); a record's trace signature is the tuple of its
  (check, outcome, rule) per applicable check, so members that disagree on a trace outcome never share a cluster.
- representatives: the lowest formKey (ordinal); a cluster of 10 or more edits adds the highest.
- rendering: per group, clusters in descending edit count until >= 80 % of the group's category-5 edits are covered.
- reason procedure: assign_reason's docstring.
- a record no built manifest holds enabled: absence_blocker's docstring (checks the load-time enable state first).
"""

import json
import os
import sys

SCHEMA = "housecarl.missdiag/1"
REASONS = (
    "not modelled",
    "not modelled (judged, saw nothing)",
    "out of scope (not run)",
    "out of scope (not run; caught when run)",
    "flagged, not joined",
    "out of scope (population)",
    "set aside by filter",
    "below threshold",
    "taste (no clash)",
    "cannot tell",
    "fixes an inter-mod clash",
    "patch placement clips",
    "patch placement hides",
    "does not fix",
)
PLANE_MARKER = "000017:Skyrim.esm"
OCCLUSION_REASONS = {"fixes an inter-mod clash": "fixes an inter-mod clash", "does not fix": "does not fix",
                     "patch placement hides": "patch placement hides",
                     "taste (no clash)": "taste (no clash)", "set aside": "set aside by filter", "cannot tell": "cannot tell"}
EVIDENCE_TYPES = ("mechanical", "check", "render reading")


def record_action(kinds):
    """Sorted '+'-join of a record's category-5 edit kinds, e.g. {'ROTATE','MOVE'} -> 'MOVE+ROTATE'."""
    return "+".join(sorted(set(kinds)))


def build_clusters(edits, bases, cells, traces):
    """edits: category-5 rows (dicts with group, formKey, kind). bases/cells/traces: dicts keyed by (group, formKey);
    traces maps to a tuple of (check, outcome, rule). Returns clusters: dicts with id, group, action, base, cell, trace,
    records (sorted formKeys), edits (count), representatives."""
    kinds, count = {}, {}
    for e in edits:
        k = (e["group"], e["formKey"])
        kinds.setdefault(k, set()).add(e["kind"])
        count[k] = count.get(k, 0) + 1
    groups = {}
    for k in sorted(kinds):
        g, fk = k
        trace = tuple(tuple(t) for t in traces.get(k, ()))
        key = (g, record_action(kinds[k]), bases.get(k), cells.get(k), trace)
        groups.setdefault(key, []).append(fk)
    clusters = []
    for (g, action, base, cell, trace), fks in groups.items():
        fks = sorted(fks)
        n = sum(count[(g, fk)] for fk in fks)
        reps = [fks[0]] + ([fks[-1]] if n >= 10 and len(fks) > 1 else [])
        clusters.append({"group": g, "action": action, "base": base, "cell": cell, "trace": trace,
                         "records": fks, "edits": n, "representatives": reps})
    clusters.sort(key=lambda c: (c["group"], -c["edits"], c["records"][0], c["action"]))
    seq = {}
    for c in clusters:
        seq[c["group"]] = seq.get(c["group"], 0) + 1
        c["id"] = "%s-%04d" % (str(c["group"]).lower(), seq[c["group"]])
    return clusters


def render_selection(clusters, coverage=0.8):
    """Cluster ids to render: per group, descending edit count (ties: cluster id) until the covered edits reach
    `coverage` of the group's edits. Returns (ids, {group: covered_fraction})."""
    ids, cov, by = set(), {}, {}
    for c in clusters:
        by.setdefault(c["group"], []).append(c)
    for g, cl in by.items():
        total = sum(c["edits"] for c in cl)
        covered = 0
        for c in sorted(cl, key=lambda c: (-c["edits"], c["id"])):
            if total and covered / total >= coverage:
                break
            ids.add(c["id"])
            covered += c["edits"]
        cov[g] = covered / total if total else 1.0
    return ids, cov


def assign_reason(ev):
    """One miss reason for a cluster. ev keys:
      rendered (bool), likelyClass (str|None; 'integration' is the taste class), classEvidence ('mechanical' |
      'check' | 'render reading' | None), check (str|None: the check modelling the class, None = no check),
      checkRunnable (bool), bundleConfiguration ('ran' | 'skipped-user' | 'not in plan'),
      trace (dict outcome, rule, emitted | None), renderShowsDefect (True | False | None),
      fixAttribution (dict | None: check, unpatched (the clash in the order without the patch), attribution (must be
      'needs both'), patched (the edit's outcome in the patched order, e.g. 'support restored' or 'item disabled')),
      placementClip (dict | None: check, item, other (another mod's object), depth (u, > 0: the least move that clears the
      crossing), pose (where the item was measured, e.g. its rest pose read in game), edit (what the patch's edit does,
      e.g. 'restores support' or 'adds the item'), relation ('crosses', the default, or 'meets': an anchor body that
      meets the other object, read with no clearance, so depth is 0 or None)).
      occlusion (dict | None: an occlusion plane cluster's verdict from the occlusion assessment, scripts/planeassess:
      verdict, statement, check, unjudgedShare?, boxLabel?).
    Returns dict(reason, detail, evidenceType, bundleConfiguration). Ordered rules, first that holds:
      00. occlusion given -> the assessment's verdict (set aside -> set aside by filter), evidence 'check', detail =
          check: statement[; unjudged share S][; Box label]. A plane is judged by what it hides, never by contact, floor
          or anchor rules. An unknown verdict or a missing part raises.
      0a. placementClip given -> patch placement clips (evidence 'check'), detail = check: item clips other by depth u at
          pose; edit: edit. A meet is worded check: item meets other at pose (a meet: depth 0); edit: edit. It outranks a
          fix attribution (an edit that restores a support but leaves its item clipping). A missing part, a crossing
          whose depth is not positive, a meet with a nonzero depth or another relation raises.
      0. fixAttribution given -> fixes an inter-mod clash (evidence 'check'), detail = check: unpatched; needs both;
         patched: outcome. An attribution other than 'needs both', or a missing part, raises (the caller must read it).
      1. likelyClass None -> cannot tell ('not rendered' if not rendered, else 'class undeterminable').
      2. likelyClass 'integration': classEvidence 'mechanical' -> taste (mechanical); else cannot tell (render reading only).
      3. check None -> not modelled (evidence = classEvidence).
      4. not checkRunnable -> out of scope (not run), detail = why.
      5. trace None -> cannot tell (trace unavailable).
      6. outcome never -> out of scope (population), detail = rule; set_aside -> set aside by filter, detail = rule;
         unjudgeable -> cannot tell, detail = rule; judged_below_threshold -> below threshold, detail = rule;
         judged_relation: emitted -> out of scope (not run; caught when run) when bundleConfiguration != 'ran', else
         flagged, not joined (a detection, not a miss; detail = trace 'notJoined': why the scorecard did not join it -
         'not needs-both', 'lane outside the scorecard's reader scope' or 'join key mismatch'; 'not stated' if absent);
         not emitted -> below threshold (admission: class);
         judged_no_relation / judged_relation_not_clash -> renderShowsDefect True -> not modelled (judged, saw
         nothing) with evidence 'render reading'; otherwise taste (no clash) with evidence 'check'.
    A render reading alone never yields taste. Any other trace outcome (absent, entered) raises: the caller must read it."""
    bundle = ev.get("bundleConfiguration")

    def out(reason, detail, evidence):
        return {"reason": reason, "detail": detail, "evidenceType": evidence, "bundleConfiguration": bundle}

    occ = ev.get("occlusion")
    if occ is not None:
        if occ.get("verdict") not in OCCLUSION_REASONS or not occ.get("statement") or not occ.get("check"):
            raise ValueError("occlusion evidence needs a known verdict, a statement and a check: %r" % (occ,))
        detail = "%s: %s" % (occ["check"], occ["statement"])
        if occ.get("unjudgedShare") is not None:
            detail += "; unjudged share %s" % occ["unjudgedShare"]
        if occ.get("boxLabel"):
            detail += "; " + occ["boxLabel"]
        return out(OCCLUSION_REASONS[occ["verdict"]], detail, "check")

    clip = ev.get("placementClip")
    if clip is not None:
        missing = [k for k in ("check", "item", "other", "pose", "edit") if not clip.get(k)]
        depth, relation = clip.get("depth"), clip.get("relation", "crosses")
        if not missing and relation == "meets" and (depth is None or depth == 0):
            return out("patch placement clips", "%s: %s meets %s at %s (a meet: depth 0); edit: %s"
                       % (clip["check"], clip["item"], clip["other"], clip["pose"], clip["edit"]), "check")
        if missing or relation != "crosses" or not isinstance(depth, (int, float)) or not depth > 0:
            raise ValueError("assign_reason: placementClip needs check, item, other, pose, edit and either a crossing with a "
                             "positive depth or a meet with depth 0; got %r" % (clip,))
        return out("patch placement clips", "%s: %s clips %s by %.2f u at %s; edit: %s"
                   % (clip["check"], clip["item"], clip["other"], depth, clip["pose"], clip["edit"]), "check")
    fix = ev.get("fixAttribution")
    if fix is not None:
        missing = [k for k in ("check", "unpatched", "attribution", "patched") if not fix.get(k)]
        if missing or fix["attribution"] != "needs both":
            raise ValueError("assign_reason: fixAttribution needs check, unpatched, attribution 'needs both' and patched; got %r" % (fix,))
        return out("fixes an inter-mod clash", "%s: %s; needs both; patched: %s" % (fix["check"], fix["unpatched"], fix["patched"]), "check")
    likely = ev.get("likelyClass")
    if likely is None:
        return out("cannot tell", "class undeterminable" if ev.get("rendered") else "not rendered", ev.get("classEvidence"))
    if likely == "integration":
        if ev.get("classEvidence") == "mechanical":
            return out("taste (no clash)", "integration rule", "mechanical")
        return out("cannot tell", "render reading only", "render reading")
    if ev.get("check") is None:
        return out("not modelled", likely, ev.get("classEvidence"))
    if not ev.get("checkRunnable"):
        return out("out of scope (not run)", ev.get("whyNotRunnable") or "the check cannot run offline", "check")
    trace = ev.get("trace")
    if trace is None:
        return out("cannot tell", "trace unavailable", "check")
    outcome, rule = trace.get("outcome"), trace.get("rule")
    if outcome == "never":
        return out("out of scope (population)", rule, "check")
    if outcome == "set_aside":
        return out("set aside by filter", rule, "check")
    if outcome == "unjudgeable":
        return out("cannot tell", rule, "check")
    if outcome == "judged_below_threshold":
        return out("below threshold", rule, "check")
    if outcome == "judged_relation":
        if trace.get("emitted"):
            if bundle != "ran":
                return out("out of scope (not run; caught when run)", rule, "check")
            return out("flagged, not joined", trace.get("notJoined") or "not stated", "check")
        return out("below threshold", "admission: class " + str(rule), "check")
    if outcome in ("judged_no_relation", "judged_relation_not_clash"):
        if ev.get("renderShowsDefect") is True:
            return out("not modelled (judged, saw nothing)", rule, "render reading")
        return out("taste (no clash)", rule, "check")
    raise ValueError("assign_reason: trace outcome %r has no rule here; the caller must read it" % (outcome,))


LOAD_ENABLE = ("enabled", "disabled", None)


def is_plane_record(base, primitive):
    """An occlusion plane: placed from the plane marker base, or carrying a placed primitive (XPRM). Such a record is
    routed to the occlusion assessment."""
    return base == PLANE_MARKER or primitive is not None


def absence_blocker(states, load_enable=None):
    """One blocker sentence for a record that no built manifest holds enabled, or None when one does.
    states: {manifest key: 'enabled' | 'absent' (in that manifest, not enabled) | 'not in manifest'}.
    load_enable: the record's load-time enable state in the unpatched order, read from the records (winning flag and
    enable chain): 'enabled', 'disabled', or None when not read. It is checked before any reason is named, because a
    manifest scoped to the responsible pair leaves out an enabled reference whose responsible plugin is outside the pair:
      'enabled'  -> outside the responsible-pair manifest (enabled at load; not measured against the pair)
      'disabled' -> not enabled in any built manifest: disabled at load
      None       -> absent from every built manifest; the load-time enable state was not read
    Any other value raises."""
    if load_enable not in LOAD_ENABLE:
        raise ValueError("absence_blocker: load_enable must be 'enabled', 'disabled' or None; got %r" % (load_enable,))
    if any(v == "enabled" for v in states.values()):
        return None
    where = "/".join(sorted(states))
    if load_enable == "enabled":
        return ("outside the responsible-pair manifest (%s): a reference enabled at load, not measured against the pair"
                % where)
    if load_enable == "disabled":
        return "not enabled in any built manifest (%s): disabled at load" % where
    return "absent from every built manifest (%s); its load-time enable state was not read" % where


def miss_table(clusters):
    """clusters: dicts with group, edits, reason, evidenceType, direct (bool: the reason rests on this cluster's own
    trace for every member, not on a representative's render). Returns rows (reason, evidenceType, directEdits,
    inheritedEdits, edits), sorted by reason then evidence type; edits sum to the input's."""
    rows = {}
    for c in clusters:
        k = (c["reason"], c["evidenceType"])
        r = rows.setdefault(k, {"reason": k[0], "evidenceType": k[1], "directEdits": 0, "inheritedEdits": 0, "edits": 0})
        r["directEdits" if c["direct"] else "inheritedEdits"] += c["edits"]
        r["edits"] += c["edits"]
    return [rows[k] for k in sorted(rows, key=lambda k: (str(k[0]), str(k[1])))]


def run(doc):
    """INPUT: {"edits": [...], "records": {"GROUP|FORMKEY": {"base", "cell", "trace": [[check, outcome, rule], ...]}},
    "evidence": {clusterId: assign_reason's input plus "direct": bool}}. A cluster without evidence is counted as
    unassigned and is never given a reason."""
    recs = doc.get("records", {})

    def key(s):
        return tuple(s.split("|", 1))

    bases = {key(k): v.get("base") for k, v in recs.items()}
    cells = {key(k): v.get("cell") for k, v in recs.items()}
    traces = {key(k): v.get("trace", ()) for k, v in recs.items()}
    clusters = build_clusters(doc["edits"], bases, cells, traces)
    ids, cov = render_selection(clusters)
    ev = doc.get("evidence", {})
    assigned = []
    for c in clusters:
        c["render"] = c["id"] in ids
        if c["id"] in ev:
            c.update(assign_reason(ev[c["id"]]))
            c["direct"] = bool(ev[c["id"]].get("direct"))
            assigned.append(c)
    return {"schema": SCHEMA, "edits": len(doc["edits"]), "clusters": clusters, "renderCoverage": cov,
            "table": miss_table(assigned), "unassignedEdits": sum(c["edits"] for c in clusters if c["id"] not in ev)}


def _runlog():
    """scripts/runlog.py, loaded by path (the harness folder is not on the repo's import path). None if absent."""
    import importlib.util
    cand = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "scripts", "runlog.py"))
    if not os.path.isfile(cand):
        return None
    spec = importlib.util.spec_from_file_location("housecarl_runlog", cand)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main(argv):
    if len(argv) != 3:
        print("usage: missdiag.py INPUT.json OUTPUT.json", file=sys.stderr)
        return 2
    rl = _runlog()
    # No child process: the run is one Run lifecycle (scripts/runlog.py), not a Runner.
    with (rl.Run("missdiag", mode="table") if rl is not None else _Null()):
        with open(argv[1], encoding="utf-8") as f:
            res = run(json.load(f))
        with open(argv[2], "w", encoding="utf-8") as f:
            json.dump(res, f, indent=1)
        print("missdiag: %d edits, %d clusters, %d unassigned edits -> %s"
              % (res["edits"], len(res["clusters"]), res["unassignedEdits"], argv[2]))
    return 0


class _Null:
    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


if __name__ == "__main__":
    sys.exit(main(sys.argv))
