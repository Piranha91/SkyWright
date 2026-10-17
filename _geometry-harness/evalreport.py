#!/usr/bin/env python
"""evalreport - compile a verified schema-2 evaluation bundle into one presentation plan and, from that
plan, a concise illustrated Markdown report, an HTML rendition, a paginated PDF with a verifiable page
limit, a complete evidence index and a presentation manifest.

No model call, no paid service, no network. Every sentence the report prints is a template sentence from
the evaluation-reporting skill's `report-priorities.json`, bound to numbers and names the bundle itself
carries: the disposition's findings, missing list, order evaluation and unassessed block; the receipts'
coverage lines; the emits' conflict rows; the registry copy's titles, claims and limits. Where the bundle
lacks something the report says "not assessed" or "not supplied" - never a guess.

Subcommands (contract: standards/HOUSECARL_EVALUATION_PRESENTATION.md):

  plan            --bundle DIR --out OUT [--mode concise|exhaustive] [--max-pages N] [--verify-copy]
  triage          --manifest ANNOTATED.json --out OUT [--policy POLICY.json]
  report          --presentation OUT/presentation.json --out OUT [--no-print]
  verify          --out OUT
  narrate-packet  --presentation P --out FILE
  narrate-apply   --presentation P --answers FILE [--out P2]

Exit codes: 0 ok; 2 a refused input (named on stderr, nothing written past the refusal record); 3 a
refused result (verify found a defect). A missing browser is `print.status: "not-rendered"` with the
reason - the Markdown and HTML are still written, and no page count is ever invented.

Q31: every subcommand opens a `scripts/runlog.py` Run (`evalreport-plan`, `evalreport-report`,
`evalreport-verify`, `evalreport-narrate`) with findings / groups / media / figures / pages as features.
"""
from __future__ import annotations

import argparse
import collections
import copy
import datetime as dt
import hashlib
import html
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402
sys.path.insert(0, str(HERE))
import evalsettings  # noqa: E402

try:
    import jinja2
except ImportError:  # pragma: no cover - stated, never worked around
    jinja2 = None

TEMPLATES = HERE / "evalreport_templates"
PRIORITIES = HERE.parent / ".claude" / "skills" / "evaluation-reporting" / "references" / "report-priorities.json"
EVALRUN = HERE / "evalrun.py"

PRESENTATION_SCHEMA = 1
BUNDLE_SCHEMA = 2
COMPILER_VERSION = "2"
EXIT_OK = 0
EXIT_REFUSED_INPUT = 2
EXIT_REFUSED_RESULT = 3

ARTIFACTS = ("report.md", "report.html", "report.pdf", "evidence-index.md", "evidence-index.html")
TRIAGE_SCHEMA = "overlap-triage-v1"
TRIAGE_POLICY = HERE.parent / ".claude" / "skills" / "world-edit-compatibility" / "references" / "overlap-triage-policy.json"


# ------------------------------------------------------------------------------------------ helpers --

def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def sha256_file(p: Path) -> str | None:
    try:
        h = hashlib.sha256()
        with open(p, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def sha256_text(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def canonical(obj) -> str:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def load_json(p: Path):
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def write_json(p: Path, obj) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=1, ensure_ascii=False)
        f.write("\n")


def write_text(p: Path, text: str) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def compile_triage_catalogue(manifest: dict, policy: dict, source_sha: str, source_path: str | None = None,
                             report_base: str | None = None) -> tuple[dict, str]:
    """Compile the triage extension through evalreport without changing the canonical manifest."""
    if manifest.get("triageSchemaVersion") != TRIAGE_SCHEMA:
        raise ValueError(f"triage-schema-unsupported: expected {TRIAGE_SCHEMA}")
    policy_sha = sha256_text(canonical(policy))
    recorded_policy = manifest.get("attributionPolicy") or {}
    recorded = (recorded_policy.get("triageRanking") or {}).get("sha256") or recorded_policy.get("sha256")
    if recorded != policy_sha:
        raise ValueError("triage-policy-changed: the supplied policy does not match the manifest; rerun overlap_triage")
    records = {str(x.get("formid")): x for x in manifest.get("records") or [] if isinstance(x, dict)}
    pairs = {str(x.get("pairKey")): x for x in manifest.get("pairs") or [] if isinstance(x, dict)}
    accounting_root = manifest.get("accounting") or {}
    accounting = dict(accounting_root.get("triage") or accounting_root)
    memberships = [key for incident in manifest.get("incidents") or [] for key in incident.get("pairKeys") or []]
    duplicates = sorted(k for k, n in collections.Counter(memberships).items() if n > 1)
    dangling = sorted(set(memberships) - set(pairs))
    unaccounted = sorted(set(pairs) - set(memberships))
    if (duplicates or dangling or unaccounted or accounting.get("pairsTotal") != len(pairs) or
            accounting.get("incidentsTotal") != len(manifest.get("incidents") or [])):
        raise ValueError(f"triage-accounting-invalid: duplicates={len(duplicates)}, dangling={len(dangling)}, "
                         f"unaccounted={len(unaccounted)}, declared={accounting.get('pairsTotal')}, actual={len(pairs)}, "
                         f"declaredIncidents={accounting.get('incidentsTotal')}, actualIncidents={len(manifest.get('incidents') or [])}")

    def is_reviewed(review):
        return isinstance(review, dict) and (review.get("status", "unreviewed") != "unreviewed" or
                                              review.get("consequence") is not None)

    def member_summary(pair):
        pair_objects = []
        for key in (pair.get("a"), pair.get("b")):
            record = records.get(str(key), {})
            fields = record.get("fields") or {}; base = fields.get("effectiveBase") or {}
            pair_objects.append({"formKey": key,
                                 "label": record.get("name") or record.get("editorid") or
                                          base.get("name") or base.get("editorid") or str(key),
                                 "type": base.get("type") or record.get("type"),
                                 "mod": fields.get("responsiblePlugin") or record.get("winner")})
        comparisons = []
        for comparison in (pair.get("attribution") or {}).get("comparisons") or []:
            changes = comparison.get("changedFields") or {}
            before_objects = {str(x.get("formid")): x for x in (comparison.get("before") or {}).get("objects") or []}
            after_objects = {str(x.get("formid")): x for x in (comparison.get("after") or {}).get("objects") or []}
            changed_values = {}
            for formid, names in changes.items():
                before, after = before_objects.get(str(formid), {}), after_objects.get(str(formid), {})
                changed_values[str(formid)] = {name: {"before": before.get(name), "after": after.get(name)}
                                               for name in names or []}
            comparisons.append({"beforeSetupId": comparison.get("beforeSetupId"),
                                "afterSetupId": comparison.get("afterSetupId"),
                                "status": comparison.get("status"), "reason": comparison.get("reason"),
                                "changedFields": {str(k): list(v or []) for k, v in changes.items() if v},
                                "changedValues": changed_values})
        features = {name: {"status": f.get("status"), "reason": f.get("reason")}
                    for name, f in (pair.get("features") or {}).items()}
        actual = pair.get("actualShape") or {}
        review = copy.deepcopy(pair.get("review") or {"status": "unreviewed", "consequence": None})
        unknown_names = sorted(name for name, feature in features.items() if feature.get("status") != "measured")
        confidence = {
            "actualShapeStatus": actual.get("status"), "intersects": actual.get("intersects"),
            "unknownFeatureNames": unknown_names,
            "analogueConfidence": sorted({str(a.get("confidence")) for a in pair.get("analogues") or [] if a.get("confidence")})}
        return {"pairKey": pair.get("pairKey"), "a": pair.get("a"), "b": pair.get("b"),
                "objects": pair_objects,
                "attribution": {"status": (pair.get("attribution") or {}).get("status"),
                                "reasons": (pair.get("attribution") or {}).get("reasons") or [],
                                "comparisons": comparisons},
                "review": review, "reviewed": is_reviewed(review), "confidenceBasis": confidence,
                "queues": copy.deepcopy(pair.get("queues") or {}),
                "analogues": [{"matchKind": a.get("matchKind"), "confidence": a.get("confidence"),
                               "sourceSetupId": a.get("sourceSetupId"),
                               "sourcePairKey": a.get("sourcePairKey"), "limits": a.get("limits") or []}
                              for a in pair.get("analogues") or []],
                "features": features}
    rows = []
    for incident in manifest.get("incidents") or []:
        members = [pairs[k] for k in incident.get("pairKeys") or [] if k in pairs]
        objects, mods, limits = [], set(), set(incident.get("limits") or [])
        comparison_counts, field_counts = collections.Counter(), collections.Counter()
        for pair in members:
            for key in (pair.get("a"), pair.get("b")):
                record = records.get(str(key), {})
                fields = record.get("fields") or {}
                base = fields.get("effectiveBase") or {}
                label = record.get("name") or record.get("editorid") or base.get("name") or base.get("editorid") or str(key)
                objects.append({"formKey": key, "label": label, "type": base.get("type") or record.get("type"),
                                "mod": fields.get("responsiblePlugin") or record.get("winner")})
                if objects[-1]["mod"]:
                    mods.add(objects[-1]["mod"])
            attr = pair.get("attribution") or {}
            for comparison in attr.get("comparisons") or []:
                comparison_counts[(comparison.get("beforeSetupId"), comparison.get("afterSetupId"),
                                   comparison.get("status") or "unresolved")] += 1
                for names in (comparison.get("changedFields") or {}).values():
                    field_counts.update(str(name) for name in names or [])
            for feature in (pair.get("features") or {}).values():
                if feature.get("status") != "measured" and feature.get("reason"):
                    limits.add(feature["reason"])
        dedup_objects = list({str(x["formKey"]): x for x in objects}.values())
        member_rows = [member_summary(p) for p in members]
        reviewed_count = sum(m["reviewed"] for m in member_rows)
        review_scope = "fully_reviewed" if member_rows and reviewed_count == len(member_rows) else \
                       "partially_reviewed" if reviewed_count else "unreviewed"
        confidence_basis = {
            "measuredActualIntersections": sum(m["confidenceBasis"]["intersects"] is True for m in member_rows),
            "unknownActualGeometry": sum(m["confidenceBasis"]["intersects"] is None for m in member_rows),
            "membersWithAnalogueContext": sum(bool(m["analogues"]) for m in member_rows),
        }
        effective_change = [f"{before}→{after}: {status} ×{count}" for (before, after, status), count in
                            sorted(comparison_counts.items(), key=lambda item: tuple(str(x) for x in item[0]))]
        if field_counts:
            effective_change.append("effective fields: " + ", ".join(
                f"{name} ×{count}" for name, count in sorted(field_counts.items())))
        rows.append({"incidentId": incident.get("incidentId"), "place": incident.get("place"),
                     "objects": dedup_objects, "mods": sorted(mods), "effectiveChange": effective_change,
                     "pairKeys": list(incident.get("pairKeys") or []), "ranking": incident.get("ranking"),
                     "review": incident.get("review"), "reviewScope": review_scope,
                     "reviewedMemberCount": reviewed_count, "unreviewedMemberCount": len(member_rows) - reviewed_count,
                     "confidenceBasis": confidence_basis,
                     "limits": sorted(limits),
                     "sourceSetups": incident.get("sourceSetups") or [],
                     "members": member_rows})
    for index, row in enumerate(rows):
        row["page"] = f"incidents-{index // 50 + 1:04d}.html"
    report_qualification = (manifest.get("scope") or {}).get("reportQualification")
    if report_qualification is not None and (not isinstance(report_qualification, str) or not report_qualification.strip()):
        raise ValueError("triage-report-qualification-invalid: scope.reportQualification must be a non-empty string")
    report_qualification = report_qualification.strip() if report_qualification else None
    identity_input = {"generator": f"evalreport-{COMPILER_VERSION}", "triageSchemaVersion": TRIAGE_SCHEMA,
                      "sourceSha256": source_sha, "policySha256": policy_sha,
                      "reportQualification": report_qualification}
    accounting.update({
        "reviewedPairs": sum(row["reviewedMemberCount"] for row in rows),
        "unreviewedPairs": sum(row["unreviewedMemberCount"] for row in rows),
        "fullyReviewedIncidents": sum(row["reviewScope"] == "fully_reviewed" for row in rows),
        "partiallyReviewedIncidents": sum(row["reviewScope"] == "partially_reviewed" for row in rows),
        "unreviewedIncidents": sum(row["reviewScope"] == "unreviewed" for row in rows),
        "outstandingReviewIncidents": sum(row["unreviewedMemberCount"] > 0 for row in rows),
        "newOrChangedGeometryUnknownPairs": sum(bool((m.get("queues") or {}).get("newOrChangedGeometryUnknown"))
                                                 for row in rows for m in row["members"]),
        "newOrChangedGeometryUnknownIncidents": sum(any((m.get("queues") or {}).get("newOrChangedGeometryUnknown")
                                                        for m in row["members"]) for row in rows),
        "pairsByAttribution": {status: sum(m["attribution"]["status"] == status for row in rows for m in row["members"])
                               for status in ("unresolved", "new", "changed_existing", "absent_excluded", "inherited")},
        "pairsWithAnalogueContext": sum(bool(m["analogues"]) for row in rows for m in row["members"]),
        "pairsDeprioritizedByAnalogue": sum(any(reason.get("quantity") == "analogue" and int(reason.get("points") or 0) < 0
                                                 for reason in (pairs[m["pairKey"]].get("ranking") or {}).get("reasons") or [])
                                            for row in rows for m in row["members"]),
    })
    catalogue = {"schemaVersion": "overlap-triage-catalogue-v1", "identity": sha256_text(canonical(identity_input)),
                 "source": identity_input, "accounting": accounting, "incidents": rows}
    catalogue["source"]["canonicalManifest"] = {"path": source_path, "sha256": source_sha}
    catalogue["source"]["reportBase"] = report_base
    base_title = (policy.get("catalogue") or {}).get("title") or "Overlap incident review catalogue"
    attribution_policy = manifest.get("attributionPolicy") or {}
    candidate_names = attribution_policy.get("candidatePlugins") or []
    comparison_ids = attribution_policy.get("comparisonSetupIds") or []
    title_scope = ", ".join(candidate_names) + ((" across " if candidate_names else "") + ", ".join(comparison_ids) if comparison_ids else "")
    title = base_title + (f" — {title_scope}" if title_scope else "")
    unknown_label = (policy.get("catalogue") or {}).get("unknownLabel") or "Unknown feature values"
    unreviewed_label = (policy.get("catalogue") or {}).get("unreviewedLabel") or "Pairs awaiting review"
    unreviewed_consequence = (policy.get("catalogue") or {}).get("unreviewedConsequence") or "No consequence established."
    next_action = (policy.get("catalogue") or {}).get("nextAction") or "Review the representative pair."
    def href_for(path):
        if not path:
            return None
        if report_base:
            return Path(os.path.relpath(Path(path).resolve(), Path(report_base).resolve())).as_posix()
        return Path(path).resolve().as_uri()
    parts = ["<!doctype html><html><head><meta charset=\"utf-8\"><title>", html.escape(title),
             "</title><style>body{font:16px/1.45 system-ui;max-width:1100px;margin:2rem auto;padding:0 1rem}"
             "table{border-collapse:collapse;width:100%}th,td{border:1px solid #bbb;padding:.45rem;vertical-align:top}"
             "th{text-align:left}code{overflow-wrap:anywhere}</style></head><body><h1>", html.escape(title), "</h1>",
             "<p>Every discovered pair remains listed under one local incident. Ranking orders review and does not decide consequence.</p>",
             (f'<p>Canonical full data: <a href="{html.escape(href_for(source_path), quote=True)}">annotated manifest</a> '
              f'<code>{html.escape(source_sha)}</code>.</p>' if source_path else
              f'<p>Canonical full data hash: <code>{html.escape(source_sha)}</code>.</p>'),
             (f'<p><strong>Scope qualification:</strong> {html.escape(report_qualification)}</p>'
              if report_qualification else ""),
             "<ul><li>Total pairs: ", str(accounting.get("pairsTotal", 0)), "</li><li>Total incidents: ",
             str(accounting.get("incidentsTotal", 0)), "</li><li>", html.escape(unreviewed_label), ": ",
             str(accounting.get("unreviewedPairs", 0)), "</li><li>", html.escape(unknown_label), ": ",
             str(accounting.get("unknownFeatureValues", 0)), " values across ", str(accounting.get("unknownPairs", 0)),
             " of ", str(accounting.get("pairsTotal", 0)), " pairs</li><li>New/changed pairs with unknown geometry: ",
             str(accounting.get("newOrChangedGeometryUnknownPairs", 0)), " of ", str(accounting.get("pairsTotal", 0)),
             " pairs, in ", str(accounting.get("newOrChangedGeometryUnknownIncidents", 0)),
             " incidents (a separately visible subset that can overlap the broader unknown-feature view)</li><li>Inherited pairs: ",
             str((accounting.get("pairsByAttribution") or {}).get("inherited", 0)), " of ", str(accounting.get("pairsTotal", 0)),
             "</li><li>Pairs with analogue context: ", str(accounting.get("pairsWithAnalogueContext", 0)), " of ",
             str(accounting.get("pairsTotal", 0)), "; ranking-deprioritized by analogue: ",
             str(accounting.get("pairsDeprioritizedByAnalogue", 0)), "</li></ul>",
             "<p>Views: <button onclick=\"showRows('all')\">All incidents (", str(accounting.get("incidentsTotal", 0)),
             ")</button> <button onclick=\"showRows('unknown')\">Incidents with unknowns (", str(sum(any(f.get("status") != "measured" for m in row["members"] for f in m["features"].values()) for row in rows)),
             ")</button> <button onclick=\"showRows('outstanding')\">Outstanding review (", str(accounting.get("outstandingReviewIncidents", 0)),
             ")</button> <button onclick=\"showRows('partially_reviewed')\">Partially reviewed (", str(accounting.get("partiallyReviewedIncidents", 0)),
             ")</button> <button onclick=\"showRows('geometry-unknown-change')\">New/changed geometry unknown (", str(accounting.get("newOrChangedGeometryUnknownIncidents", 0)),
             ")</button></p><table><thead><tr><th>Incident and place</th><th>Objects and mods</th><th>Effective change</th>"
             "<th>Review order, status and next action</th><th>Limits and evidence</th></tr></thead><tbody>"]
    for row in rows:
        place = row.get("place") or {}; cell = place.get("cell") or {}
        place_text = cell.get("editorid") or cell.get("formid") or place.get("space") or "place not supplied"
        object_text = "; ".join(f"{x.get('label')} ({x.get('type') or 'type unknown'}, {x.get('mod') or 'mod unknown'})"
                                for x in row["objects"])
        ranking = row.get("ranking") or {}; review = row.get("review") or {}
        rank_reasons = "; ".join(f"{x.get('quantity')}={x.get('value')} ({int(x.get('points') or 0):+d})"
                                 for x in ranking.get("reasons") or [])
        consequence = review.get("consequence") or unreviewed_consequence
        row_next_action = (f"Review each of the {row['unreviewedMemberCount']} outstanding member pairs in fixed-camera source and combined scenes; "
                           "a representative review applies only to that pair."
                           if row["unreviewedMemberCount"] else
                           (review.get("nextAction") or "Every member pair has an individual review."))
        confidence = row.get("confidenceBasis") or {}
        confidence_text = (f"confidence basis: {confidence.get('measuredActualIntersections', 0)} measured intersections; "
                           f"{confidence.get('unknownActualGeometry', 0)} geometry unknown; "
                           f"{confidence.get('membersWithAnalogueContext', 0)} with analogue context")
        unknown = any(f.get("status") != "measured" for member in row["members"] for f in member["features"].values())
        queue_unknown = any((m.get("queues") or {}).get("newOrChangedGeometryUnknown") for m in row["members"])
        row_class = "unknown " if unknown else ""
        row_class += row["reviewScope"] + " "
        row_class += "outstanding " if row["unreviewedMemberCount"] else ""
        row_class += "geometry-unknown-change" if queue_unknown else ""
        source_parts = []
        for source_setup in row["sourceSetups"]:
            source_path = source_setup.get("manifestPath")
            label = html.escape(str(source_setup.get("setupId")))
            if source_path:
                label = f'<a href="{html.escape(href_for(source_path), quote=True)}">{label}</a>'
            source_parts.append(f"{label}: {html.escape(str(source_path or 'path unavailable'))} "
                                f"[{html.escape(str(source_setup.get('manifestSha256') or 'hash unavailable'))}]")
        sources = "; ".join(source_parts)
        detail_link = f'{row["page"]}#{html.escape(str(row.get("incidentId")), quote=True)}'
        parts.extend(["<tr class=\"", row_class, "\"><td><strong>", html.escape(str(row.get("incidentId"))), "</strong><br>", html.escape(str(place_text)),
                      "<br><small>", str(len(row["pairKeys"])), " pair(s)</small></td><td>", html.escape(object_text),
                      "</td><td>", html.escape(", ".join(row["effectiveChange"])), "</td><td>score ",
                      html.escape(str(ranking.get("score"))), " — ", html.escape(rank_reasons), "<br>",
                      html.escape(row["reviewScope"]), " (", str(row["reviewedMemberCount"]), " reviewed; ",
                      str(row["unreviewedMemberCount"]), " outstanding): ", html.escape(str(consequence)),
                      "<br>", html.escape(row_next_action),
                      "</td><td>", html.escape(confidence_text), "<br>", html.escape("; ".join(row["limits"])), "<p>", sources or "No source setup link supplied.",
                      f'</p><a href="{detail_link}">Every member pair and source-evidence citation</a></td></tr>'])
    parts.extend(["</tbody></table><p><small>Catalogue identity <code>", catalogue["identity"],
                  "</code>; source <code>", source_sha, "</code>; policy <code>", policy_sha,
                  "</code>.</small></p><script>function showRows(k){document.querySelectorAll('tbody tr').forEach(r=>r.hidden=k!=='all'&&!r.classList.contains(k));}</script></body></html>"])
    return catalogue, "".join(parts)


def render_triage_incident_pages(catalogue: dict, page_size: int = 50) -> dict[str, str]:
    """Render bounded detail pages while retaining every incident and pair membership."""
    rows = catalogue.get("incidents") or []
    total_pages = max(1, (len(rows) + page_size - 1) // page_size)
    rendered = {}
    for page_index in range(total_pages):
        subset = rows[page_index * page_size:(page_index + 1) * page_size]
        nav = " ".join(f'<a href="incidents-{n:04d}.html">{n}</a>' for n in range(1, total_pages + 1))
        parts = ["<!doctype html><html><head><meta charset=\"utf-8\"><title>Overlap incident details</title>",
                 "<style>body{font:16px/1.45 system-ui;max-width:1100px;margin:2rem auto;padding:0 1rem}"
                 "pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f5f5f5;padding:.75rem}"
                 "article{border-top:1px solid #bbb;margin-top:2rem}</style></head><body>",
                 '<p><a href="overlap-triage-catalogue.html">Catalogue index</a></p>',
                 (f'<p><strong>Scope qualification:</strong> '
                  f'{html.escape(str((catalogue.get("source") or {}).get("reportQualification")))}</p>'
                  if (catalogue.get("source") or {}).get("reportQualification") else ""),
                 f"<p>Detail page {page_index + 1} of {total_pages}; {len(subset)} incidents on this page. Pages: {nav}</p>"]
        for row in subset:
            members = row.get("members") or []
            page_unknown = sum(any(f.get("status") != "measured" for f in (m.get("features") or {}).values()) for m in members)
            page_unreviewed = row.get("unreviewedMemberCount", 0)
            member_summaries = []
            for member in members:
                labels = " ↔ ".join(f"{x.get('label')} [{x.get('formKey')}, {x.get('mod') or 'mod unknown'}]"
                                    for x in member.get("objects") or [])
                comparisons = "; ".join(
                    f"{c.get('beforeSetupId')}→{c.get('afterSetupId')}: {c.get('status')}"
                    for c in (member.get("attribution") or {}).get("comparisons") or [])
                review = member.get("review") or {}
                consequence = review.get("consequence") or "unreviewed"
                member_summaries.append("<li><strong>" + html.escape(str(member.get("pairKey"))) +
                    "</strong>: " + html.escape(labels) + "<br>" + html.escape(comparisons) +
                    "<br>Consequence: " + html.escape(str(consequence)) + "</li>")
            parts.extend([f'<article id="{html.escape(str(row.get("incidentId")), quote=True)}"><h2>',
                          html.escape(str(row.get("incidentId"))), "</h2><p>", str(len(members)),
                          " member pairs; review scope: ", html.escape(str(row.get("reviewScope"))),
                          "; unreviewed members: ", str(page_unreviewed),
                          "; member pairs with unknown features: ", str(page_unknown),
                          ". Global totals: ", str(catalogue.get("accounting", {}).get("pairsTotal", 0)),
                          " pairs, ", str(catalogue.get("accounting", {}).get("unreviewedPairs", 0)),
                          " unreviewed.</p><ul>", "".join(member_summaries),
                          "</ul><details><summary>Full member evidence, changes, confidence and review data</summary><pre>",
                          html.escape(json.dumps(members, indent=2, ensure_ascii=False)),
                          "</pre></details></article>"])
        parts.append("</body></html>")
        rendered[f"incidents-{page_index + 1:04d}.html"] = "".join(parts)
    return rendered


def read_text(p: Path) -> str:
    return p.read_text(encoding="utf-8", errors="replace")


def refuse(run: Run | None, reason: str, code: int = EXIT_REFUSED_INPUT) -> int:
    print(f"REFUSED: {reason}", file=sys.stderr)
    if run is not None:
        run.refuse(reason, exit_code=code)
    return code


def fmt_n(v) -> str:
    if isinstance(v, bool) or v is None:
        return str(v)
    if isinstance(v, int):
        return f"{v:,}"
    if isinstance(v, float):
        return f"{v:,.2f}"
    return str(v)


def arm_sort_key(arm_id: str, kinds: dict | None = None) -> tuple:
    kind = (kinds or {}).get(arm_id)
    return (0 if (arm_id == "baseline" or kind == "baseline") else 1, arm_id)


PART_ORDER = {None: 0, "arm": 1, "reverse": 2, "diff": 3}


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", str(text).lower()).strip("-")


def load_priorities() -> dict:
    pr = load_json(PRIORITIES)
    if not isinstance(pr, dict):
        raise SystemExit(f"REFUSED: report-priorities.json is absent or not JSON at {PRIORITIES}")
    return pr


# ------------------------------------------------------------------------------------ the browser --

WINDOWS_BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Chromium\Application\chrome.exe",
]


def browser_version(exe: Path) -> str:
    # Chrome and Edge on Windows keep the version as a sibling directory of the executable; asking the
    # binary itself prints nothing usable in headless mode. Elsewhere `--version` answers.
    try:
        vers = sorted(d.name for d in exe.parent.iterdir()
                      if d.is_dir() and re.fullmatch(r"\d+\.\d+\.\d+\.\d+", d.name))
        if vers:
            return vers[-1]
    except OSError:
        pass
    if os.name != "nt":
        try:
            out = subprocess.run([str(exe), "--version"], capture_output=True, text=True, timeout=20)
            if out.stdout.strip():
                return out.stdout.strip()
        except (OSError, subprocess.SubprocessError):
            pass
    return "unknown"


def resolve_browser() -> tuple[Path | None, str, str]:
    """Resolution order (D4): HOUSECARL_BROWSER, PATH chrome / msedge / chromium, the standard Windows
    install paths. Returns (path, version, reason-when-absent)."""
    env = (os.environ.get("HOUSECARL_BROWSER") or "").strip()
    if env:
        p = Path(env)
        if p.exists():
            return p, browser_version(p), ""
        return None, "", f"HOUSECARL_BROWSER points at {env}, which does not exist"
    for name in ("chrome", "google-chrome", "google-chrome-stable", "msedge", "chromium", "chromium-browser"):
        w = shutil.which(name)
        if w:
            return Path(w), browser_version(Path(w)), ""
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        cands = list(WINDOWS_BROWSERS)
        if local:
            cands.append(str(Path(local) / "Google" / "Chrome" / "Application" / "chrome.exe"))
        for c in cands:
            if Path(c).exists():
                return Path(c), browser_version(Path(c)), ""
    return None, "", ("no Chromium browser found: set HOUSECARL_BROWSER, or put chrome / msedge / chromium "
                      "on PATH, or install Chrome or Edge at the standard location")


def pdf_page_count(pdf: Path) -> tuple[int | None, int | None, str]:
    """Count `/Type /Page` objects (not `/Pages`) in the PDF's uncompressed page tree and cross-check
    against the root `/Pages` node's `/Count`. Chrome's Skia backend writes the page tree uncompressed.
    Returns (pages, rootCount, note); pages is None when nothing countable was found."""
    try:
        data = pdf.read_bytes()
    except OSError as ex:
        return None, None, f"unreadable: {ex}"
    if not data.startswith(b"%PDF"):
        return None, None, "not a PDF (no %PDF header)"
    pages = len(re.findall(rb"/Type\s*/Page\b", data))
    counts = []
    for m in re.finditer(rb"\d+\s+\d+\s+obj\b(.*?)endobj", data, re.S):
        body = m.group(1)
        if re.search(rb"/Type\s*/Pages\b", body):
            c = re.search(rb"/Count\s+(\d+)", body)
            if c:
                counts.append(int(c.group(1)))
    root = max(counts) if counts else None
    if pages == 0 and root is None:
        return None, None, "no page objects visible (object streams?) - the count could not be established"
    if root is not None and root != pages:
        return pages, root, f"page objects {pages} disagree with the root /Count {root}"
    return pages, root, "ok"


def print_pdf(browser: Path, html: Path, pdf: Path, timeout: int = 180) -> tuple[bool, str]:
    profile = Path(tempfile.mkdtemp(prefix="evalreport-browser-"))
    try:
        argv = [str(browser), "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                f"--user-data-dir={profile}", f"--print-to-pdf={pdf.resolve()}", html.resolve().as_uri()]
        try:
            r = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            return False, f"the browser did not finish within {timeout}s"
        except OSError as ex:
            return False, f"the browser could not be started: {ex}"
        if not pdf.exists() or pdf.stat().st_size == 0:
            tail = (r.stderr or "")[-400:].strip().replace("\n", " | ")
            return False, f"the browser exited {r.returncode} and wrote no PDF: {tail or 'no stderr'}"
        return True, ""
    finally:
        shutil.rmtree(profile, ignore_errors=True)


# ------------------------------------------------------------------------------ bundle verification --

def run_verify_bundle(bundle: Path, out: Path, copy: bool) -> tuple[dict | None, int, str]:
    """D1: `evalrun.py verify-bundle DIR` is the authority. With --verify-copy the bundle is copied under
    OUT first so the original directory is not written to (verify-bundle writes DIR/verify.json); the
    copy verifies identically because verify-bundle reads nothing outside the bundle."""
    target = bundle
    tmp = None
    if copy:
        tmp = out / ".verify-copy"
        shutil.rmtree(tmp, ignore_errors=True)
        shutil.copytree(bundle, tmp)
        target = tmp
    try:
        r = subprocess.run([sys.executable, str(EVALRUN), "verify-bundle", str(target)],
                           capture_output=True, text=True, timeout=900)
    except (OSError, subprocess.SubprocessError) as ex:
        if tmp:
            shutil.rmtree(tmp, ignore_errors=True)
        return None, EXIT_REFUSED_INPUT, f"verify-bundle could not run: {ex}"
    verify = load_json(target / "verify.json")
    if isinstance(verify, dict):
        write_json(out / "verify.json", verify)
    if tmp:
        shutil.rmtree(tmp, ignore_errors=True)
    if not isinstance(verify, dict):
        return None, r.returncode, ("verify-bundle wrote no readable verify.json: " +
                                    (r.stderr or r.stdout or "")[-400:].strip())
    return verify, r.returncode, ""


# -------------------------------------------------------------------------------------- compile --

class Bundle:
    """Read-only view of a schema-2 bundle: manifest, disposition, plan, ledger, registry, receipts."""

    def __init__(self, d: Path):
        self.dir = d
        self.manifest = load_json(d / "manifest.json") or {}
        self.disposition = load_json(d / self.manifest.get("disposition", "disposition.json")) or {}
        self.plan = load_json(d / self.manifest.get("plan", "plan.json")) or {}
        self.ledger = load_json(d / self.manifest.get("ledger", "ledger.json")) or {}
        self.registry = load_json(d / self.manifest.get("registry", "registry/check-registry.json")) or {}
        self.scope = load_json(d / self.manifest["scope"]) if self.manifest.get("scope") else None
        self.checks = {c.get("id"): c for c in (self.registry.get("checks") or []) if isinstance(c, dict)}
        self.families = {f.get("id"): f for f in (self.registry.get("families") or []) if isinstance(f, dict)}
        self.exe_to_check = {}
        for cid, c in self.checks.items():
            exe = (c.get("adapter") or {}).get("exe")
            if exe and exe not in self.exe_to_check:
                self.exe_to_check[exe] = cid
        self.evidence = [e for e in (self.manifest.get("evidence") or []) if isinstance(e, dict)]
        self.by_path = {e["path"]: e for e in self.evidence if e.get("path")}
        self.receipts = {}
        for e in self.evidence:
            if e.get("kind") == "receipt":
                rc = load_json(d / e["path"])
                if isinstance(rc, dict) and rc.get("receiptId"):
                    self.receipts[rc["receiptId"]] = rc
        self.receipt_by_key = {}
        for rid, rc in self.receipts.items():
            self.receipt_by_key[(rc.get("checkId"), rc.get("armId"), rc.get("part"))] = rc
        self._emits = {}

    def emit(self, path: str):
        if path not in self._emits:
            self._emits[path] = load_json(self.dir / path)
        return self._emits[path]

    def check_title(self, cid: str) -> str:
        c = self.checks.get(cid) or {}
        return c.get("title") or cid

    def check_family(self, cid: str) -> str:
        c = self.checks.get(cid) or {}
        return c.get("family") or (cid.split(".")[0] if cid else "")

    def evidence_for(self, check_id: str, arm_id: str, part: str | None) -> list[dict]:
        """Manifest evidence rows recorded by the receipt of (check, arm, part), plus the receipt row."""
        rc = self.receipt_by_key.get((check_id, arm_id, part))
        rows = []
        if rc:
            rid = rc["receiptId"]
            for e in self.evidence:
                if e.get("recordedBy") == rid or ((e.get("provenance") or {}).get("receiptId") == rid):
                    rows.append(e)
            rp = f"receipts/{check_id}__{arm_id}" + (f"__{part}" if part else "") + ".json"
            if rp in self.by_path:
                rows.append(self.by_path[rp])
        return rows

    def output_emit_path(self, check_id: str, arm_id: str, part: str | None) -> str | None:
        """The file a check's rows are read from: an import's `rows` output (copied as `NN-rows.json`), else rows
        derived after measurement from that import's transcripts (a `record-rows` enrichment), else the first JSON
        output the receipt recorded."""
        rc = self.receipt_by_key.get((check_id, arm_id, part))
        if not rc:
            return None
        rid = rc["receiptId"]
        emits = [e["path"] for e in self.evidence if e.get("kind") == "emit" and e.get("recordedBy") == rid
                 and str(e.get("path", "")).endswith(".json")]
        rows = [x for x in emits if x.endswith("-rows.json")]
        if rows:
            return rows[0]
        if part in (None, "arm"):
            for e in self.evidence:
                if (e.get("kind") == "enrichment" and e.get("enrichment") == "record-rows"
                        and e.get("checkId") == check_id and e.get("armId") == arm_id):
                    return e["path"]
        return emits[0] if emits else None

    def enrichment(self) -> list[dict]:
        return [e for e in self.evidence if e.get("kind") == "enrichment"]

    def diff_log_path(self, check_id: str, arm_id: str) -> str | None:
        rc = self.receipt_by_key.get((check_id, arm_id, "diff"))
        if not rc:
            return None
        rid = rc["receiptId"]
        for e in self.evidence:
            if e.get("recordedBy") == rid and str(e.get("path", "")).endswith(".txt"):
                return e["path"]
        return None


DIFF_LINE = re.compile(r"^\s*(NEW|GREW|SHRANK|GONE|SAME|UNCHANGED)\s+(\S+)\s+(.+?)\s{2,}x\s{2,}(.+?)\s{2,}([+-]?\d+)\s+record\(s\):\s*(.*?)\s*$")


def parse_diff_log(text: str) -> list[dict]:
    out = []
    for line in text.splitlines():
        m = DIFF_LINE.match(line)
        if not m:
            continue
        tail = m.group(6)
        nums = {k: int(v) for k, v in re.findall(r"(repaired|reclassified|created|unmeasured)\s+(\d+)", tail)}
        out.append({"change": m.group(1), "kind": m.group(2), "A": m.group(3).strip(), "B": m.group(4).strip(),
                    "delta": int(m.group(5)), "line": line.strip(), **nums})
    return out


def conflict_rows(conflict: dict, evidence_id: str | None) -> list[dict]:
    rows = []
    # the partner objects a geometry row is measured against (an overlap's other object, a fixture's removed host):
    # with the row's own object they identify the relationship whatever plugins own the two objects
    alternative = conflict.get("Alternative")
    other = (sorted(str(x.get("FormKey")) for x in alternative if isinstance(x, dict) and x.get("FormKey"))
             if isinstance(alternative, list) and conflict.get("Kind") != "LOST-EDIT" else None)
    for n in conflict.get("Necessary") or []:
        if not isinstance(n, dict):
            continue
        rows.append({"formKey": n.get("FormKey"), "label": n.get("Label"), "summary": conflict.get("Summary"),
                     "physical": n.get("Physical"), "physicalDetail": n.get("PhysicalDetail"),
                     "evidenceId": evidence_id,
                     **({"other": other} if other is not None else {}),
                     **({"field": conflict.get("Field"), "values": n.get("Values"), "reads": n.get("Reads")}
                        if conflict.get("Kind") == "LOST-EDIT" else {})})
    if not rows:
        rows.append({"formKey": None, "label": None, "summary": conflict.get("Summary"), "physical": None,
                     "physicalDetail": None, "evidenceId": evidence_id})
    return rows


def infer_clobber_direction(finding: dict, plugin_order: list[str]) -> dict | None:
    c = finding.get("counts") or {}
    if c.get("groupKind") != "CLOBBERED" or not c.get("A") or not c.get("B"):
        return None
    a_name, b_name = str(c["A"]), str(c["B"])
    rows = finding.get("allRows") or finding.get("rows") or []
    summaries = [str(r.get("summary") or "") for r in rows]
    details = " ".join(str(r.get("physicalDetail") or "") for r in rows)
    direction_pattern = re.compile(r"re-cut by\s+" + re.escape(a_name) + r"\s+and then re-cut again by\s+"
                                   + re.escape(b_name) + r"(?:\s|,)", re.I)
    stated = bool(summaries) and all(direction_pattern.search(s) for s in summaries)
    named_winners = {m.group(1) for m in re.finditer(r"winner's \(([^)]+)\)", details, re.I)}
    winner_name = b_name if not named_winners or {x.lower() for x in named_winners} == {b_name.lower()} else None
    folded = {str(n).lower(): i for i, n in enumerate(plugin_order)}
    ordered = (winner_name is not None and a_name.lower() in folded and winner_name.lower() in folded
               and folded[a_name.lower()] < folded[winner_name.lower()])
    if not stated or not ordered:
        return None
    return {"overwrittenPlugin": a_name, "winningPlugin": winner_name, "order": "earlier-to-later",
            "basis": "conflict evidence and recorded plugin order"}


def bind(template: str, values: dict) -> str:
    class Safe(dict):
        def __missing__(self, k):
            return "not supplied"
    vals = {k: (fmt_n(v) if isinstance(v, (int, float)) and not isinstance(v, bool) else
                ("not supplied" if v is None else v)) for k, v in values.items()}
    return template.format_map(Safe(vals))


IMPORT_ORIGINS = ("fresh", "reused")
PREREQUISITE_STATUS = re.compile(r"^blocked\(prerequisite:([^)]+)\)$")


def imported_evidence(b: Bundle) -> dict:
    """Where each credited MCP import's evidence came from.

    WHICH imports are credited: the disposition's `importedEvidence` list when the bundle carries one; a bundle
    finalized before that list existed has none, and then an accepted receipt with an `imported` block whose
    (check, arm) is a completed result in the disposition counts. WHAT the origin is: the receipt's own
    `imported.origin` when the bundle carries the receipt (the controller's recovery authority), else the
    disposition row's. Anything but fresh or reused - including a receipt written before an origin had to be
    declared - is `unknown`, never fresh. `reused` is the import's declaration that the evidence was captured for
    another evaluation; the ledger's own `reused` status (this run re-using its receipt on resume) is not read."""
    by_rid = {rid: rc for rid, rc in b.receipts.items() if isinstance(rc.get("imported"), dict)}
    ev = b.disposition.get("evaluation") or {}
    listed = b.disposition.get("importedEvidence")
    if isinstance(listed, list):
        source = "disposition"
        credited = [(r.get("checkId"), r.get("armId"), r.get("receiptId"), r) for r in listed if isinstance(r, dict)]
    else:
        source = "receipts"
        completed = {(r.get("checkId"), r.get("armId")) for key in ("findings", "completedNullChecks", "completedCatalogues")
                     for r in ev.get(key) or [] if isinstance(r, dict)}
        credited = sorted(((rc.get("checkId"), rc.get("armId"), rid, {}) for rid, rc in by_rid.items()
                           if (rc.get("validation") or {}).get("accepted") and (rc.get("checkId"), rc.get("armId")) in completed),
                          key=lambda x: (str(x[0]), str(x[1])))
    rows = []
    for check_id, arm_id, rid, drow in credited:
        imp = (by_rid.get(rid) or {}).get("imported")
        declared = imp.get("origin") if isinstance(imp, dict) else drow.get("origin")
        origin = declared if declared in IMPORT_ORIGINS else "unknown"
        imp = imp if isinstance(imp, dict) else {}
        row = {"checkId": check_id, "armId": arm_id, "receiptId": rid, "origin": origin,
               "capturedUtc": imp.get("capturedUtc") or drow.get("capturedUtc"),
               "tool": imp.get("tool"), "sourceSha256": imp.get("sourceSha256"),
               "receiptPath": f"receipts/{check_id}__{arm_id}.json"}
        if origin == "fresh":
            row["originCheck"] = imp.get("originCheck")
        elif origin == "reused":
            row["reusedFrom"] = imp.get("reusedFrom") or drow.get("reusedFrom")
            row["reusedFromVerified"] = bool(imp.get("reusedFromVerified", drow.get("reusedFromVerified")))
            row["reusedFromNote"] = imp.get("reusedFromNote")
            within = imp.get("reusedWithin") or drow.get("reusedWithin")
            row["reusedWithin"] = within if within in ("this-evaluation", "another-evaluation") else "unstated"
            # What the capture says about the file it was measured against, in the same three states the
            # controller records (bundle contract 4.3, source rule): `other-file` names another candidate,
            # `unstated` means the capture names none at all, and no key means the names agree and there is
            # nothing extra to tell the reader. `unstated` is a limitation in its own right and must survive to
            # the page - filtering on a truthy filename dropped it silently (Piranha's handback, 2026-09-17).
            # Read from the identity limit the controller wrote, never re-derived here. The bundle's own
            # receipt is the authority; the disposition row is the fallback when this reader lacks the receipt.
            lim = next((x for x
                        in ((by_rid.get(rid) or {}).get("validation") or {}).get("identityLimits") or []
                        if isinstance(x, dict) and x.get("armCandidate")), None)
            if lim is not None:
                row["sourceDisclosure"] = "other-file" if lim.get("capturedAgainst") else "unstated"
                row["capturedAgainst"] = lim.get("capturedAgainst") or None
                row["armCandidate"] = lim.get("armCandidate")
            elif drow.get("sourceDisclosure") in ("other-file", "unstated"):
                row["sourceDisclosure"] = drow["sourceDisclosure"]
                row["capturedAgainst"] = drow.get("capturedAgainst") or None
                row["armCandidate"] = drow.get("armCandidate")
        rows.append(row)
    return {"source": source if rows or isinstance(listed, list) else None, "rows": rows}


def is_base_game(name: str, words: dict) -> bool:
    """A Bethesda file: the listed base-game files, or a Creation Club plugin (`cc*.esm/esp/esl`)."""
    n = str(name or "").lower()
    listed = {str(x).lower() for x in words.get("baseGamePlugins") or []}
    prefix = str(words.get("baseGamePrefix") or "").lower()
    return n in listed or bool(prefix and n.startswith(prefix) and n.endswith((".esm", ".esp", ".esl")))


class InputRefused(Exception):
    """An input the compiler will not use, with the one-sentence reason; `plan` exits 2 with it."""


def build_prediction(ctx: dict) -> dict:
    """The prediction, coverage and validation blocks a report leads with.

    ctx: findings (planned, with allRows), armOrders {armId: [plugin]}, armIds (setups-compared order), targets
    [name], declared (lower-case declared patches), comparisonKinds, armChanges {armId: (added, removed)}, missing,
    unassessed, checkTitles, missingReason (fn), oldVerdict, limitations, words (report-priorities reader.prediction),
    inGame (reader.inGame), kindPhrases.

    The prediction setup P is the one with the evaluated mods and no patch: the arm that removes a declared patch when
    one does, else the baseline. W has none of the evaluated mods; S(m) has only m. A row is keyed
    (check, Kind, A, B, FormKey); presence is read from each setup's own conflict file (part `arm` or none)."""
    words, findings = ctx["words"], ctx["findings"]
    lower = lambda xs: {str(x).lower() for x in xs if x}                          # noqa: E731
    target_lower = lower(ctx["targets"])
    orders = ctx["armOrders"]
    kinds = ctx["comparisonKinds"]
    changes = ctx["armChanges"]
    declared = ctx["declared"]

    # --- which setup is P, and which (if any) carries the patch
    patched_arm, p_arm, patch_names = None, "baseline", []
    comparisons = []
    for arm_id in ctx["armIds"]:
        if kinds.get(arm_id) != "patch-comparison":
            continue
        added, removed = changes.get(arm_id) or ([], [])
        if removed and not added:
            comparisons.append({"withPatch": "baseline", "withoutPatch": arm_id, "patches": list(removed)})
        elif added and not removed:
            comparisons.append({"withPatch": arm_id, "withoutPatch": "baseline", "patches": list(added)})
    if comparisons:
        main = max(comparisons, key=lambda c: len(c["patches"]))
        p_arm, patched_arm, patch_names = main["withoutPatch"], main["withPatch"], main["patches"]
    p_set = lower(orders.get(p_arm) or [])
    p_targets = [t for t in ctx["targets"] if str(t).lower() in p_set]
    pt_lower = lower(p_targets)

    replaced_by = ctx.get("armReplaced") or {}

    def arm_with(plugins_lower):
        for arm_id in ctx["armIds"]:
            # A same-named variant has the plain setup's names but not its files (SW2): never take it as that setup.
            if replaced_by.get(arm_id):
                continue
            if arm_id != p_arm and lower(orders.get(arm_id) or []) == plugins_lower:
                return arm_id
        return None
    # Every setup's plugins, in this bundle and (below) the sibling: a plugin that is loaded in exactly the setups
    # that load a given mod travels with it (its own resources or masters), so the setup without that mod also lacks
    # it. A target or a declared patch never travels with anything.
    setups_seen = [lower(v) for v in orders.values()]
    declared_lower = {str(x).lower() for x in declared}

    def companions(drop_lower):
        out = set()
        for d in drop_lower:
            with_d = [s_ for s_ in setups_seen if d in s_]
            without_d = [s_ for s_ in setups_seen if d not in s_]
            if not with_d or not without_d:
                continue
            for c in set.intersection(*with_d) - set.union(*without_d) if without_d else set():
                if c != d and c not in target_lower and c not in declared_lower:
                    out.add(c)
        return out

    def arm_without(drop_lower):
        """This bundle's setup equal to P without these plugins (and, failing that, without their companions too)."""
        return arm_with(p_set - drop_lower) or arm_with(p_set - drop_lower - companions(drop_lower))
    w_arm = arm_without(pt_lower) if p_targets else None
    s_arms = {t: arm_without(pt_lower - {str(t).lower()}) for t in p_targets} if len(p_targets) > 1 else {}

    # --- per (check, arm): measured rows, measured with no conflict file, or nothing
    rows_by, prefixes, measured, count_only = {}, {}, set(), {}
    ident_of = {}
    by_object = set(words.get("identityByObject") or [])

    def identity(check_id, kind, a, b, r):
        """What makes two rows on two setups the same clash. A record lost edit is one plugin's change to one field
        of one record lost to another plugin: the pair and the field belong to it. A geometry row with a partner
        object is the relationship between two objects, whichever plugins own them on each setup. A check whose row
        is a state of its own object (skill data `identityByObject`: a fixture left hanging, an object left without
        support) is that object: a fixture hanging because both evaluated mods remove hosts hangs with one of them
        alone if that one removes enough. Any other row keeps its plugin pair."""
        if kind == "LOST-EDIT":
            return (check_id, kind, a, b, r.get("formKey"), r.get("field"))
        if check_id in by_object:
            return (check_id, kind, r.get("formKey"))
        if r.get("other") is not None:
            return (check_id, kind, r.get("formKey"), tuple(r.get("other") or ()))
        return (check_id, kind, a, b, r.get("formKey"))

    def collect(fs, prefix=""):
        for f in fs:
            if f.get("part") not in ("arm", None):
                continue
            key = (f.get("checkId"), prefix + str(f.get("armId")))
            if f.get("kind") == "null":
                measured.add(key)
            elif f.get("kind") == "finding":
                measured.add(key)
                c = f.get("counts") or {}
                if c.get("derivedRows") == 0:
                    continue
                if not (c.get("A") and c.get("B")):
                    count_only[key] = count_only.get(key, 0) + int(c.get("findings") or 0)
                    continue
                for r in f.get("allRows") or f.get("rows") or []:
                    # one entry per reported row (a record can carry several conflicts); presence elsewhere is read
                    # on the record and its pair, the first five parts of the key
                    rk = (f.get("checkId"), str(c.get("groupKind") or ""), str(c.get("A")), str(c.get("B")),
                          r.get("formKey"), str(r.get("summary") or ""))
                    rows_by.setdefault(key, {})[rk] = {"finding": f, "row": r,
                                                       "history": f.get("readerEvidenceReason") == "context-only-kind"}
                    ident = identity(rk[0], rk[1], rk[2], rk[3], r)
                    ident_of.setdefault(rk, ident)
                    prefixes.setdefault(key, set()).add(ident)
    collect(findings)

    # --- the sibling: a separate evaluation of the same order without the patch, whose single-mod setups stand in
    #     for the ones this bundle lacks. Strict by design: its setup must be this bundle's P plugin for plugin,
    #     hash for hash, and a check is used only where both ran the same adapter and probe build and measured the
    #     same rows on that shared setup. Otherwise nothing is joined (refusal) or the check stays unseparated.
    sibling, sib_ok, sibling_record = ctx.get("sibling"), {}, None
    sib_role_ref = []
    sib_only, anchor = set(), None
    if sibling is not None and p_targets:
        main_b, sib_b = ctx["mainBundle"], sibling["bundle"]

        def ident(bundle, arm_id):
            a = next((x for x in bundle.plan.get("arms") or [] if x.get("armId") == arm_id), {}) or {}
            req = a.get("requested") or {}
            plugs = tuple((str(x.get("name")), str(x.get("sha256") or "")) for x in req.get("plugins") or [])
            states = tuple(req.get("states") or (a.get("expected") or {}).get("states") or [])
            return plugs, req.get("population"), states

        p_plugs, p_pop, p_states = ident(main_b, p_arm)
        sib_idents = {x.get("armId"): ident(sib_b, x.get("armId")) for x in sib_b.plan.get("arms") or []}
        anchor = next((a for a, v in sib_idents.items() if v == (p_plugs, p_pop, p_states)), None)
        if anchor is None:
            raise InputRefused("sibling-foreign: the sibling bundle has no setup with the same plugins, in the same "
                               "order and with the same bytes, as the setup without the patch, so nothing was joined.")
        collect(sibling["findings"], "sibling:")
        setups_seen.extend({x[0].lower() for x in v[0]} for v in sib_idents.values())

        def sib_role(drop_lower):
            for drop in (drop_lower, drop_lower | companions(drop_lower)):
                want = (tuple(x for x in p_plugs if x[0].lower() not in drop), p_pop, p_states)
                found = next((a for a, v in sib_idents.items() if v == want), None)
                if found:
                    return found
            return None
        sib_role_ref.append(sib_role)
        roles = {}
        if w_arm is None:
            w_sib = sib_role(pt_lower)
            if w_sib:
                w_arm = roles["without the evaluated mods"] = "sibling:" + w_sib
        for t in list(s_arms):
            if s_arms[t] is None:
                s_sib = sib_role(pt_lower - {str(t).lower()})
                if s_sib:
                    s_arms[t] = roles[f"only {t}"] = "sibling:" + s_sib

        def receipt_tool(bundle, check_id, arm_id):
            rc = bundle.receipt_by_key.get((check_id, arm_id, "arm")) or bundle.receipt_by_key.get((check_id, arm_id, None))
            t = ((rc or {}).get("consumed") or {}).get("tool") or {}
            return t.get("dllSha256"), t.get("coreDllSha256")

        def receipt_config(bundle, check_id, arm_id):
            rc = bundle.receipt_by_key.get((check_id, arm_id, "arm")) or bundle.receipt_by_key.get((check_id, arm_id, None))
            cfg = ((rc or {}).get("consumed") or {}).get("configuration") or {}
            plugs = cfg.get("plugins")
            return None if not isinstance(plugs, list) else tuple(
                (str(x.get("name")), str(x.get("sha256") or "")) for x in plugs if isinstance(x, dict))

        checks_record = []
        for check_id in sorted({c for c, _ in measured if c}):
            why, how = None, None
            if (main_b.checks.get(check_id) or {}).get("adapter") != (sib_b.checks.get(check_id) or {}).get("adapter"):
                why = "the two evaluations ran this check with different adapters"
            elif (check_id, p_arm) not in measured and (check_id, "sibling:" + anchor) in measured:
                # Only the sibling measured it on the shared setup: its rows stand in for this bundle's, and only when
                # the configuration its own receipt records is this setup's, plugin for plugin, in order, byte for byte.
                got = receipt_config(sib_b, check_id, anchor)
                if got is None:
                    why = ("the evaluation without the patch records no configuration for this check on the shared "
                           "setup, so its result is not used")
                elif got != p_plugs:
                    why = ("the evaluation without the patch measured this check on a configuration that differs from "
                           "this setup's (plugins, order or bytes), so its result is not used")
                else:
                    how = "from-sibling-only"
                    sib_only.add(check_id)
            elif (check_id, p_arm) not in measured or (check_id, "sibling:" + anchor) not in measured:
                why = "one of the two evaluations did not measure this check on the shared setup"
            else:
                mt, st = receipt_tool(main_b, check_id, p_arm), receipt_tool(sib_b, check_id, anchor)
                if mt[0] and st[0] and mt != st:
                    why = "the two evaluations ran this check with different probe builds"
                elif (prefixes.get((check_id, p_arm)) or set()) != (prefixes.get((check_id, "sibling:" + anchor)) or set()):
                    why = "the two evaluations measured different results for this check on the shared setup"
            sib_ok[check_id] = why is None
            checks_record.append({"checkId": check_id, "joined": why is None, "why": why,
                                  **({"how": how} if how else {})})
        sibling_record = {"pathAsGiven": sibling.get("pathAsGiven"), "planId": sibling.get("planId"),
                          "manifestSha256": sibling.get("manifestSha256"), "anchorArm": anchor,
                          "roles": {k: v.split(":", 1)[1] for k, v in roles.items()}, "checks": checks_record}

    def p_of(check_id):
        """The setup a check's prediction rows come from: this bundle's P, or the sibling's identical setup when only
        the sibling measured the check there."""
        return "sibling:" + anchor if check_id in sib_only else p_arm

    def present(arm_id, rk):
        if arm_id is not None and str(arm_id).startswith("sibling:") and not sib_ok.get(rk[0]):
            return False                       # a sibling check that does not match on the shared setup is unused
        if arm_id == p_arm:
            arm_id = p_of(rk[0])
        return arm_id is not None and ident_of.get(rk, tuple(rk[:5])) in (prefixes.get((rk[0], arm_id)) or set())

    def admission(rk, history):
        a, b = rk[2], rk[3]
        if history:
            return "history"
        if a.lower() == b.lower():
            return "sameMod"
        if present(w_arm, rk):
            return "alreadyPresent"
        if is_base_game(a, words) or is_base_game(b, words):
            return "baseGame"
        if not ({a.lower(), b.lower()} & pt_lower):
            return "notInvolving"
        return "admitted"

    without_cache = {}

    def without_other(m_lower):
        """The setup equal to P without one other mod (and its companions), in this bundle or the sibling."""
        if m_lower not in without_cache:
            found = arm_without({m_lower})
            if found is None and sib_role_ref:
                s_ = sib_role_ref[0]({m_lower})
                found = "sibling:" + s_ if s_ else None
            without_cache[m_lower] = found
        return without_cache[m_lower]

    def attribution(rk):
        sides = [x for x in (rk[2], rk[3]) if x.lower() in pt_lower]
        if len(p_targets) <= 1:
            # One evaluated mod t, a clash with another mod m: does it need m too? Only the setup without m can say
            # (Snazzy review, P38): absent there, it needs both; present there, it comes from t; not measured, the
            # report says it appears when t is added and no more.
            t = sides[0] if sides else None
            others = [x for x in (rk[2], rk[3]) if x.lower() not in pt_lower]
            if not t or not others:
                return "single-mod", t, None
            arm_m = without_other(others[0].lower())
            if (arm_m is None or (rk[0], arm_m) not in measured
                    or (str(arm_m).startswith("sibling:") and not sib_ok.get(rk[0]))):
                return "with-target", t, None
            return ("single-mod" if present(arm_m, rk) else "needs-both"), t, None
        if any(str(s_arms.get(t)).startswith("sibling:") for t in p_targets) and not sib_ok.get(rk[0]):
            return "not-separable", None, words["notSeparableWhy"]["siblingDisagrees"]
        unmeasured = [t for t in p_targets if s_arms.get(t) is None or (rk[0], s_arms[t]) not in measured]
        if unmeasured:
            if all(s_arms.get(t) is None for t in p_targets):
                why = words["notSeparableWhy"]["noSingleSetups"]
            else:
                why = bind(words["notSeparableWhy"]["notMeasured"], {"mod": unmeasured[0]})
            return "not-separable", None, why
        # an object both evaluated mods break may be broken by either alone; the mod the row names comes first
        alone = sorted((t for t in p_targets if present(s_arms[t], rk)), key=lambda t: t.lower() not in
                       {rk[2].lower(), rk[3].lower()})
        if alone:
            return "single-mod", alone[0], None
        return "needs-both", None, None

    record, set_aside = [], collections.Counter()
    lost_seen = set()
    groups = collections.OrderedDict()
    multi = len(p_targets) > 1
    single_mode = ctx.get("singleMod") or words.get("singleModDefault") or "show"
    unit = ctx.get("headlineUnit") or words.get("headlineUnit") or "relationships"
    p_rows = {}
    for (check_id, arm_id), rs in rows_by.items():
        if arm_id == p_of(check_id):
            p_rows.update(rs)
    for rk in sorted(p_rows, key=lambda k: tuple(str(x) for x in k)):
        hit = p_rows[rk]
        adm = admission(rk, hit["history"])
        entry = {"checkId": rk[0], "kind": rk[1], "A": rk[2], "B": rk[3], "formKey": rk[4], "summary": rk[5],
                 "admission": adm,
                 "findingId": hit["finding"].get("id")}
        if rk[0] in sib_only:
            # its finding lives in the sibling's plan, not this one: named, never linked as if it were here
            entry["source"] = "sibling"
            entry["siblingFindingId"] = entry.pop("findingId")
            entry["findingId"] = None
        if hit["row"].get("other") is not None:
            entry["other"] = list(hit["row"]["other"])
        if hit["row"].get("physical") is not None:
            entry["physical"] = hit["row"]["physical"]
        if hit["row"].get("field") is not None:
            entry["field"] = hit["row"]["field"]
            entry["values"] = hit["row"].get("values")
            # Two record checks read overlapping populations (a cell's fields are in both the contested-fields and
            # the cell-attributes check): the same lost edit is one relationship, counted under the first check.
            same = (rk[2].lower(), rk[3].lower(), rk[4], entry["field"])
            if adm == "admitted" and same in lost_seen:
                adm = entry["admission"] = "repeat"
            lost_seen.add(same)
        if adm != "admitted":
            set_aside[adm] += 1
            record.append(entry)
            continue
        attr, attr_mod, why = attribution(rk)
        entry.update({"attribution": attr, "attributionMod": attr_mod})
        if attr == "single-mod" and multi and single_mode == "aside":
            # a clash one evaluated mod has with a third mod on its own: set aside by the switch (Piranha's R1)
            entry["admission"] = "singleMod"
            set_aside["singleMod"] += 1
            record.append(entry)
            continue
        record.append(entry)
        mods = sorted([rk[2], rk[3]], key=str.lower)
        gk = (rk[0], rk[1], tuple(mods), attr, attr_mod, why)
        g = groups.setdefault(gk, {"checkId": rk[0], "kind": rk[1], "mods": mods, "count": 0, "attribution": attr,
                                   "attributionMod": attr_mod, "attributionWhy": why, "findingIds": [], "examples": [],
                                   "_subjects": set()})
        g["count"] += 1
        g["_subjects"].add(rk[4] if rk[4] is not None else ("row", rk[5]))
        fid = hit["finding"].get("id") if rk[0] not in sib_only else None
        if fid and fid not in g["findingIds"]:
            g["findingIds"].append(fid)
        label = hit["row"].get("label")
        if label and len(g["examples"]) < 2 and label not in g["examples"]:
            g["examples"].append(label)
        # what makes the item actionable: the direction a lost edit goes, example records, the places it is in,
        # and whether its count rests on a result reused from another evaluation
        g.setdefault("_directions", set()).add((rk[2], rk[3]))
        if rk[1] == "LOST-EDIT" and rk[4] and len(g.setdefault("_records", [])) < 2:
            ex = f"{rk[4]} ({hit['row'].get('field')})" if hit["row"].get("field") else str(rk[4])
            if ex not in g["_records"]:
                g["_records"].append(ex)
        if label and rk[1] != "LOST-EDIT":
            # every row's place counts, a place named by form key included; a navmesh row's triangle note is not
            # part of its place
            g.setdefault("_places", collections.Counter())[re.sub(r"\s+\d+ tris -> \d+$", "", str(label))] += 1
        if hit["finding"].get("readerProvenance"):
            # the full sentence is in the limits and beside the finding; the item says it in short
            g["_provenance"] = words.get("itemReused") or hit["finding"]["readerProvenance"]

    titles, in_game, phrases = ctx["checkTitles"], ctx["inGame"], ctx["kindPhrases"]

    def title_of(check_id):
        return titles.get(str(check_id)) or ctx["checkTitle"](check_id)

    def reason_words(code, check_id=None):
        # a probe's reason in the skill's words; a reason already written in words passes through; a code the skill
        # does not name gets one generic phrase, never the code (the code stays in the evidence index). A count the
        # adapter reads with no reason ("unjudgeable") is worded for its check where the skill names it.
        code = str(code or "").strip()
        if code in ("", "unjudgeable") and check_id and (words.get("unjudgeableByCheck") or {}).get(check_id):
            return words["unjudgeableByCheck"][check_id]
        known = (words.get("unjudgeableWords") or {}).get(code)
        if known:
            return known
        if " " in code:
            return code
        return words["unjudgeableUnnamed"]
    items, subjects_of = [], {}
    for g in groups.values():
        g["title"] = title_of(g["checkId"])
        g["inGame"] = in_game.get(f"{g['checkId']}|{g['kind']}") or in_game.get(g["checkId"])
        ph = phrases.get(g["kind"]) or {}
        noun = (ph.get("unitSingular") if g["count"] == 1 else ph.get("unitPlural")) or ("clash" if g["count"] == 1 else "clashes")
        attr_key = g["attribution"]
        other_mod = next((m for m in g["mods"] if str(m).lower() != str(g["attributionMod"] or "").lower()), "")
        if not multi and attr_key in ("needs-both", "single-mod", "with-target") and g["attributionMod"]:
            attr_key = {"needs-both": "needs-both-other", "single-mod": "single-mod-target",
                        "with-target": "with-target"}[attr_key]
        elif (attr_key == "single-mod" and g["attributionMod"]
                and str(g["attributionMod"]).lower() not in {m.lower() for m in g["mods"]}):
            attr_key = "single-mod-elsewhere"       # the same object is broken with the other evaluated mod alone
        attr_text = bind(words["attribution"][attr_key], {"mod": g["attributionMod"], "other": other_mod,
                                                          "why": g["attributionWhy"] or ""})
        subjects_of[id(g)] = g.pop("_subjects")
        g["relationships"] = g["count"]
        g["objects"] = len(subjects_of[id(g)])
        # a kind whose rows are fields of a record (a path, not a thing) also says how many records they are on
        on = (f", on {fmt_n(g['objects'])} {ph['objectsSingular'] if g['objects'] == 1 else ph['objectsPlural']},"
              if ph.get("objectsPlural") else "")
        directions = g.pop("_directions", set())
        detail = []
        if g["kind"] == "LOST-EDIT" and len(directions) == 1:
            loser, winner = next(iter(directions))
            detail.append(bind(words["itemDirection"], {"loser": loser, "winner": winner}))
        if g.get("_records"):
            detail.append(bind(words["itemExamples"], {"examples": "; ".join(g["_records"])}))
        places = g.pop("_places", None)
        if places:
            top = places.most_common(2)
            if len(places) == 1:
                detail.append(bind(words["itemPlaceOne" if g["count"] == 1 else "itemPlaceAll"], {"place": top[0][0]}))
            else:
                detail.append(bind(words["itemPlaces"], {"places": "; ".join(f"{pl} ({fmt_n(n)})" for pl, n in top),
                                                        "more": (bind(words["itemPlacesMore"], {"count": len(places) - len(top)})
                                                                 if len(places) > len(top) else "")}))
        g.pop("_records", None)
        provenance = g.pop("_provenance", None)
        g["plain"] = (f"{fmt_n(g['count'])} {noun}{on} between {g['mods'][0]} and {g['mods'][1]}. "
                      + attr_text + (f" {ph['readerQualification']}" if ph.get("readerQualification") else "")
                      + "".join(" " + d for d in detail) + (f" {provenance}" if provenance else ""))
        g["inGameText"] = (f"In game: {g['inGame']}." if g["inGame"] else words["inGameMissing"])
        g["shownAs"] = ("pair" if not multi or g["attribution"] == "needs-both" else
                        "alone" if g["attribution"] == "single-mod" else "unseparated")
        items.append(g)
    # the pair's clashes first, then the ones that could not be separated, then those of one mod alone (own heading)
    shown_rank = {"pair": 0, "unseparated": 1, "alone": 2}
    items.sort(key=lambda g: (shown_rank[g["shownAs"]], -g["count"], g["checkId"], g["mods"]))
    # P53: the clashes already there with one mod alone, one line per check: each pair with its count and the mod it
    # comes from; the check's in-game sentence once
    alone_lines = collections.OrderedDict()
    for g in items:
        if g["shownAs"] != "alone":
            continue
        ph = phrases.get(g["kind"]) or {}
        mod = str(g["attributionMod"] or "")
        elsewhere = mod.lower() not in {m.lower() for m in g["mods"]}
        third = next((m for m in g["mods"] if m.lower() != mod.lower()), g["mods"][0]) if not elsewhere else \
            " and ".join(g["mods"])
        line = alone_lines.setdefault(g["checkId"], {"title": g["title"], "inGameText": g["inGameText"],
                                                     "unit": ph.get("unitPlural") or "clashes", "groups": {}})
        key = bind(words["aloneFrom" + ("Elsewhere" if elsewhere else "")], {"mod": mod})
        line["groups"].setdefault(key, []).append({"text": f"{third} {fmt_n(g['count'])}",
                                                   "findingIds": list(g["findingIds"])})

    subject_mods = p_targets or list(ctx["targets"])
    subject = (" and ".join([", ".join(subject_mods[:-1]), subject_mods[-1]]) + " together"
               if len(subject_mods) > 1 else (subject_mods[0] if subject_mods else "the evaluated mod"))
    # The number counts what needs both evaluated mods (with one evaluated mod, every admitted clash is its own).
    # A clash one mod has with a third mod on its own is shown under its own heading or set aside, never counted
    # here; a clash whose cause could not be separated is stated in one clause with its count. Each kind of clash
    # (a check and its kind) is stated in its own unit and never summed with another (Gate C round 1): objects and
    # record fields are different things. A kind whose rows are fields counts records in the verdict (skill data
    # `verdictKinds`); its field count stays in its item's line.
    vk = words["verdictKinds"]

    def per_kind(gs):
        out = collections.OrderedDict()
        for g in gs:
            e = out.setdefault((g["checkId"], g["kind"]), {"checkId": g["checkId"], "kind": g["kind"],
                                                           "relationships": 0, "_s": set()})
            e["relationships"] += g["relationships"]
            e["_s"] |= subjects_of[id(g)]
        return [{**{k: v for k, v in e.items() if k != "_s"}, "objects": len(e["_s"])} for e in out.values()]

    def stated(entries):
        out = []
        for e in entries:
            w = vk.get(f"{e['checkId']}|{e['kind']}") or vk.get(e["checkId"]) or vk["default"]
            u = w.get("unit") or unit
            n = e[u]
            nouns = w.get(u) or vk["default"][u]
            out.append({**e, "unit": u, "count": n, "phrase": f"{fmt_n(n)} " + bind(nouns[0] if n == 1 else nouns[1],
                                                                                   {"title": title_of(e["checkId"])})})
        return sorted(out, key=lambda e: (-e["count"], e["checkId"], e["kind"]))

    def listed(parts):
        return parts[0] if len(parts) == 1 else "; ".join(parts[:-1]) + "; and " + parts[-1]
    by_attr = {k: [g for g in items if g["attribution"] == k]
               for k in ("needs-both", "single-mod", "not-separable", "with-target")}
    counts = {key: per_kind(by_attr[a])
              for key, a in (("needsBoth", "needs-both"), ("singleMod", "single-mod"), ("notSeparable", "not-separable"))}
    counted = by_attr["needs-both"] if multi else items
    verdict_counts = stated(per_kind(counted))
    unseparated = stated(counts["notSeparable"]) if multi else []
    checks_on_p = sorted({c for (c, a) in measured if a == p_of(c)})
    if verdict_counts:
        verdict = bind(words["verdictFound"], {"subject": subject, "kinds": listed([e["phrase"] for e in verdict_counts])})
    else:
        verdict = bind(words["verdictNone"], {"subject": subject})
    if unseparated:
        verdict += " " + bind(words["verdictNotSeparable"], {"kinds": listed([e["phrase"] for e in unseparated])})
    missing_facts = []
    if not p_targets:
        missing_facts.append(words["noModInSetup"])
    elif len(p_targets) > 1 and all(v is None for v in s_arms.values()) and not items:
        missing_facts.append(bind(words["attribution"]["not-separable"], {"why": words["notSeparableWhy"]["noSingleSetups"]}))
    aside = {"baseGame": set_aside["baseGame"], "sameMod": set_aside["sameMod"],
             "alreadyPresent": set_aside["alreadyPresent"], "history": set_aside["history"],
             "notInvolving": set_aside["notInvolving"], "singleMod": set_aside["singleMod"],
             "repeat": set_aside["repeat"]}
    prediction = {"subject": subject, "mods": subject_mods, "predictionArm": p_arm, "withoutArm": w_arm,
                  "singleArms": s_arms, "verdict": verdict, "status": "found" if items else "none-found",
                  "items": items, "aloneLines": [{**v, "groups": [{"from": k, "entries": e} for k, e in v["groups"].items()]}
                                                 for v in alone_lines.values()],
                  "verdictCounts": verdict_counts, "counts": counts, "headlineUnit": unit,
                  "singleModMode": single_mode, "singleModHeading": words.get("singleModHeading"),
                  "missingFacts": list(dict.fromkeys(missing_facts)),
                  "setAside": aside,
                  "setAsideLine": bind(words["setAsideLine"], aside) if sum(aside.values()) else None,
                  "noneFoundBasis": None if items else bind(words["noneFoundBasis"], {
                      "checks": len(checks_on_p), "checkNoun": "check" if len(checks_on_p) == 1 else "checks",
                      "subject": subject}),
                  "record": record, "detailAsideWords": words.get("detailAside") or {},
                  "sibling": sibling_record,
                  "siblingSource": words.get("siblingSource") if sibling_record and sibling_record["roles"] else None}

    # --- coverage: what the checks could not judge, by named reason and count
    reasons = words["coverageReasons"]
    lines = []
    relevant_arms = {p_arm, patched_arm, w_arm, *s_arms.values()} - {None}
    statuses_of, arms_of = collections.OrderedDict(), {}
    for m in ctx["missing"]:
        if m.get("armId") in relevant_arms:
            statuses_of.setdefault(str(m.get("checkId")), [])
            if str(m.get("status")) not in statuses_of[str(m.get("checkId"))]:
                statuses_of[str(m.get("checkId"))].append(str(m.get("status")))
            arms_of.setdefault(str(m.get("checkId")), []).append((m.get("armId"), m.get("part")))
    by_status = collections.OrderedDict()
    for check_id, sts in statuses_of.items():
        by_status.setdefault(tuple(sts), collections.OrderedDict())[check_id] = arms_of[check_id]
    labels = ctx["armLabels"]
    all_relevant = len(relevant_arms)
    for status_set, by_check in by_status.items():
        reason = "; and ".join(ctx["missingReason"](st, "has no accepted result") for st in status_set)
        subjects = []
        for check_id, arms_ in sorted(by_check.items(), key=lambda kv: title_of(kv[0])):
            # a missing comparison part is the comparison with the measured order, not the setup's own result
            def place(a, part):
                label = labels.get(a)
                # it follows "for" / "of" mid-sentence: lower-cased, with its article
                label = ("the " + label[:1].lower() + label[1:]) if label else "a tested setup"
                return (f"the comparison of {label} with the measured load order" if part in ("diff", "reverse")
                        else label)
            setups = sorted({(a, part) for a, part in arms_ if a}, key=lambda x: (str(x[0]), str(x[1])))
            where = ("" if len({a for a, _ in setups}) >= all_relevant and all(pt not in ("diff", "reverse")
                                                                                for _, pt in setups) else
                     " (for " + "; ".join(place(a, part) for a, part in setups) + ")")
            subjects.append(title_of(check_id) + where)
        lines.append({"reason": bind(reasons["missing"], {"reason": reason}), "count": len(by_check),
                      "unit": "check" if len(by_check) == 1 else "checks", "subject": "; ".join(subjects)})
    # What P's own measurement could not judge, read from P's own receipt (the disposition's list also carries
    # each comparison's receipts, which counted one population two or three times), one clause per population in
    # its own unit, never summed across populations. A reason that names another check is not "could not be
    # judged": it is judged there, and said so on its own line.
    elsewhere_re = re.compile(r"^(.*?):\s*judged by ([\w.-]+), not field-compared here$")

    def own_coverage(check_id):
        arm_id, bundle = p_of(check_id), ctx.get("mainBundle")
        if str(arm_id).startswith("sibling:"):
            arm_id, bundle = arm_id.split(":", 1)[1], (sibling or {}).get("bundle")
        rc = None
        if bundle is not None:
            rc = bundle.receipt_by_key.get((check_id, arm_id, "arm")) or bundle.receipt_by_key.get((check_id, arm_id, None))
        cov = ((rc or {}).get("result") or {}).get("coverage") if rc else None
        own = [(c.get("Population", c.get("population")), c.get("Unjudgeable", c.get("unjudgeable")))
               for c in cov if isinstance(c, dict)] if isinstance(cov, list) else []
        if any((sum(v for v in uj.values() if isinstance(v, (int, float))) if isinstance(uj, dict) else uj or 0) > 0
               for _, uj in own):
            return own
        seen, out = set(), []                  # no receipt here: the disposition's rows for P, each population once
        for u in ctx["unassessed"].get("unjudgeablePopulations") or []:
            if u.get("armId") == p_arm and u.get("checkId") == check_id and u.get("population") not in seen:
                seen.add(u.get("population"))
                out.append((u.get("population"), u.get("unjudgeable")))
        return out
    unjudged_checks = list(dict.fromkeys(
        [str(u.get("checkId")) for u in ctx["unassessed"].get("unjudgeablePopulations") or [] if u.get("armId") == p_arm]
        + list(checks_on_p)))
    elsewhere = collections.Counter()

    def emit_unjudged(check_id):
        arm_id, bundle = p_of(check_id), ctx.get("mainBundle")
        if str(arm_id).startswith("sibling:"):
            arm_id, bundle = arm_id.split(":", 1)[1], (sibling or {}).get("bundle")
        if bundle is None:
            return None
        path = bundle.output_emit_path(check_id, arm_id, "arm") or bundle.output_emit_path(check_id, arm_id, None)
        emit = bundle.emit(path) if path else None
        ul = emit.get("Unjudged") if isinstance(emit, dict) else None
        return ul if isinstance(ul, list) else None
    for check_id in unjudged_checks:
        # a check whose rows file lists its unjudged fields gets the fields line below, which says more; its
        # populations line would count the same fields twice (judged-elsewhere reasons are still read here)
        fields_line = any(isinstance(u, dict) and u.get("Field") for u in emit_unjudged(check_id) or [])
        pops = []
        for population, uj in own_coverage(check_id):
            per = collections.Counter()
            for k, v in (uj.items() if isinstance(uj, dict) else [("unjudgeable", uj)]):
                if not (isinstance(v, (int, float)) and not isinstance(v, bool) and v > 0):
                    continue
                m_ = elsewhere_re.match(str(k))
                if m_:
                    elsewhere[(m_.group(1).strip(), title_of(m_.group(2)))] += v
                    continue
                per[reason_words(k, check_id)] += v
            if per:
                pops.append((population, per))
        if not pops or fields_line:
            continue

        def detail_of(per):
            top = per.most_common()
            shown = top[:3]
            text = "; ".join(r if len(top) == 1 else f"{r} {fmt_n(n)}" for r, n in shown)
            if len(top) > len(shown):
                text += "; " + bind(reasons["unjudgeableMore"], {"count": len(top) - len(shown)})
            return text
        if len(pops) == 1:
            total_n = sum(pops[0][1].values())
            lines.append({"reason": reasons["unjudgeable"], "count": total_n,
                          "unit": "item" if total_n == 1 else "items",
                          "subject": f"{title_of(check_id)}: {detail_of(pops[0][1])}"})
        else:
            lines.append({"reason": reasons["unjudgeable"], "count": None,
                          "subject": f"{title_of(check_id)}: " + "; ".join(
                              f"{fmt_n(sum(per.values()))} {population or 'items'} ({detail_of(per)})"
                              for population, per in pops)})
    if elsewhere:
        lines.append({"reason": reasons["judgedElsewhere"], "count": sum(elsewhere.values()),
                      "unit": "item" if sum(elsewhere.values()) == 1 else "items",
                      "subject": "; ".join(bind(reasons["judgedElsewhereItem"], {"what": what, "check": t, "count": fmt_n(n)})
                                           for (what, t), n in elsewhere.items())})
    # The fields a check left unjudged on P, as its rows file lists them (a record-rows enrichment's `Unjudged`):
    # counted with why, and how many lie on records both evaluated mods change, where a clash between them could
    # hide (Gate C round 1, M1).
    for check_id in checks_on_p:
        ul = [u for u in emit_unjudged(check_id) or [] if isinstance(u, dict)]
        fields = [u for u in ul if u.get("Field") and not elsewhere_re.match(str(u.get("Reason") or ""))]
        if not fields and not [u for u in ul if not u.get("Field")]:
            continue
        per = collections.Counter(reason_words(u.get("Reason")) for u in fields)
        parts = [f"{r} {fmt_n(n)}" for r, n in per.most_common()]
        unreadable_records = [u for u in ul if not u.get("Field")]
        if unreadable_records:
            parts.append(bind(words["recordsUnreadable"], {"count": fmt_n(len(unreadable_records))}))
        if multi:
            both = sum(1 for u in fields if pt_lower <= {str(x).lower() for x in u.get("Plugins") or []})
            parts.append(bind(words["fieldsUnjudgedBoth"], {"count": fmt_n(both)}))
        lines.append({"reason": reasons["fieldsUnjudged"], "count": len(fields),
                      "unit": "field" if len(fields) == 1 else "fields",
                      "subject": f"{title_of(check_id)}: " + "; ".join(parts)})
    not_sep = sum(g["count"] for g in items if g["attribution"] == "not-separable")
    if not_sep:
        lines.append({"reason": reasons["notSeparable"], "count": not_sep,
                      "subject": "; ".join(sorted({title_of(g["checkId"]) for g in items
                                                   if g["attribution"] == "not-separable"}))})
    co = {c: n for (c, a), n in count_only.items() if a == p_arm}
    if co:
        lines.append({"reason": reasons["countOnly"], "count": sum(co.values()),
                      "subject": "; ".join(sorted(title_of(c) for c in co))})
    un = ctx["unassessed"]
    states_p = [x for x in un.get("statesNotExplored") or []
                if not isinstance(x, dict) or x.get("armId") in (None, p_arm, p_of(x.get("checkId")))]
    if states_p:
        state_words = words.get("stateClassWords") or {}

        def state_clause(x):
            if not isinstance(x, dict):
                return str(x)
            what = state_words.get(str(x.get("code") or x.get("subject"))) or str(x.get("reason") or x.get("subject"))
            n_ = x.get("count")
            return f"{what} ({fmt_n(n_)})" if isinstance(n_, int) else f"{what} ({words['stateExtentUnknown']})"
        lines.append({"reason": reasons["statesNotExplored"], "count": len(states_p),
                      "subject": "; ".join(dict.fromkeys(state_clause(x) for x in states_p))})
    if un.get("scopeUnmeasured"):
        # the controller's extent ids in the skill's words; an id it does not name gets one generic phrase (the ids
        # stay in the evidence index)
        extent_words = words.get("extentWords") or {}
        areas = list(dict.fromkeys(extent_words.get(str(x.get("extent"))) or words["extentUnnamed"]
                                   for x in un["scopeUnmeasured"] if isinstance(x, dict)))
        lines.append({"reason": reasons["scopeUnmeasured"], "count": len(un["scopeUnmeasured"]),
                      "subject": "; ".join(areas)})
    cannot_run = [c for c in ctx.get("cannotRunChecks") or [] if c not in statuses_of and (c, p_of(c)) not in measured]
    if cannot_run:
        lines.append({"reason": reasons["checksCannotRun"], "count": len(cannot_run),
                      "subject": "; ".join(sorted(title_of(c) for c in cannot_run))})
    coverage = {"lines": lines, "notes": list(ctx["limitations"]), "setAsideLine": prediction["setAsideLine"],
                "checksOnPrediction": len(checks_on_p), "relevantArms": sorted(relevant_arms)}

    # --- validation against the patch
    if patched_arm is None:
        validation = {"status": "no-patch", "headline": ctx["oldVerdict"], "patches": [],
                      "sentence": words["validationNone"],
                      "other": bind(words["validationOtherSetups"], {"headline": ctx["oldVerdict"]}),
                      "hits": None, "open": None, "falseAlarms": None, "patchProblems": None, "blindSpots": [],
                      "byItem": [], "missingFacts": []}
    else:
        # several patches are named once (the scope line and the setups list name them all) and called "the N
        # patches" in each sentence after (P53: six names repeated in every line took a page)
        patch = ", ".join(patch_names) if len(patch_names) <= 2 else bind(words["patchesShort"], {"count": len(patch_names)})
        admitted = [e for e in record if e["admission"] == "admitted"]
        keyof = lambda e: (e["checkId"], e["kind"], e["A"], e["B"], e["formKey"], e["summary"])  # noqa: E731
        not_measured = [e for e in admitted if (e["checkId"], patched_arm) not in measured]
        judged = [e for e in admitted if (e["checkId"], patched_arm) in measured]
        patched_rows = {k: v for (c, a), rs in rows_by.items() if a == patched_arm for k, v in rs.items()}
        patch_lower = {str(x).lower() for x in patch_names}
        # A lost edit (record lane) is not judged by presence: with the patch loaded last the same plugin's change can
        # still be lost, to the patch. Four outcomes: resolved (no change to the field is lost with the patch, or the
        # patch wins with a value other than the winning value without it); kept on purpose (the patch wins with the
        # same winning value: the patch author confirmed the winner, whichever mod's value that is); left (the
        # field's winner with the patch is not a patch: nothing touched it); cannot tell (the patch wins, but a
        # winning value is not recorded on one side, so the two cannot be compared). A lost edit whose winner is the
        # patch is the patch's choice, never a problem the patch causes.
        lost_w = collections.defaultdict(list)
        for rk, hit in patched_rows.items():
            if rk[1] == "LOST-EDIT":
                lost_w[(rk[0], rk[4], hit["row"].get("field"))].append(hit["row"] | {"winnerPlugin": rk[3]})

        unrecorded = {str(x) for x in words.get("unrecordedValues") or []}

        def recorded(v):
            if v is None or v == "" or v == []:
                return False
            vs = v if isinstance(v, list) else [v]
            return not any(str(x) in unrecorded for x in vs)

        def lost_outcome(e):
            rows_w = lost_w.get((e["checkId"], e["formKey"], e.get("field")))
            if not rows_w:
                return "resolved"              # no plugin's change to the field is lost with the patch
            w = rows_w[0]
            if str(w["winnerPlugin"]).lower() not in patch_lower:
                return "left"
            with_patch, without = (w.get("values") or {}).get("winner"), (e.get("values") or {}).get("winner")
            if not (recorded(with_patch) and recorded(without)):
                return "cannot-tell-value"
            return "resolved" if with_patch != without else "kept"
        # A geometry row is judged by its own predicate re-measured with the patch, where the probe carries one (an
        # emit's `Predicates`: every row of the setup it was measured against, measured again in this setup): Gone is
        # resolved, Holds is left (whoever owns the objects now), anything else cannot be told. Presence of the row
        # (by its identity) is the rule only for a check whose emits carry no predicates.
        pred_cache = {}

        def predicates(check_id):
            if check_id not in pred_cache:
                pred_cache[check_id] = None
                bundle = ctx.get("mainBundle")
                if bundle is not None and p_of(check_id) == p_arm:
                    where = ((patched_arm, "arm") if p_arm == "baseline" else
                             (p_arm, "reverse") if patched_arm == "baseline" else None)
                    path = None
                    if where:
                        path = bundle.output_emit_path(check_id, *where) or (
                            bundle.output_emit_path(check_id, where[0], None) if where[1] == "arm" else None)
                    emit = bundle.emit(path) if path else None
                    preds = emit.get("Predicates") if isinstance(emit, dict) else None
                    if isinstance(preds, list):
                        pred_cache[check_id] = {(x.get("Kind"), x.get("A"), x.get("B"), x.get("FormKey"), x.get("Other")):
                                                str(x.get("Physical")) for x in preds if isinstance(x, dict)}
            return pred_cache[check_id]

        diff_arm = patched_arm if p_arm == "baseline" else (p_arm if patched_arm == "baseline" else None)

        def geometry_outcome(e):
            if ((ctx.get("comparisonState") or {}).get((e["checkId"], diff_arm)) or {}).get("refused"):
                return "cannot-tell"           # the comparison with and without the patch refused a verdict
            preds = predicates(e["checkId"])
            if preds is None:
                return "left" if present(patched_arm, keyof(e)) else "resolved"
            other = e.get("other") or []
            physical = preds.get((e["kind"], e["A"], e["B"], e["formKey"], other[0] if other else None))
            return {"Gone": "resolved", "Holds": "left"}.get(physical, "cannot-tell")

        outcome = {id(e): lost_outcome(e) if e["kind"] == "LOST-EDIT" else geometry_outcome(e) for e in judged}
        hits = [e for e in judged if outcome[id(e)] == "resolved"]
        kept = [e for e in judged if outcome[id(e)] == "kept"]
        open_ = [e for e in judged if outcome[id(e)] == "left"]
        untold = [e for e in judged if outcome[id(e)] == "cannot-tell"]
        untold_value = [e for e in judged if outcome[id(e)] == "cannot-tell-value"]
        for e in admitted:
            e["withPatch"] = outcome.get(id(e), "not-measured")
        problems, choices, problem_finding = [], 0, {}
        for rk, hit in patched_rows.items():
            if rk[1] == "LOST-EDIT" and rk[3].lower() in patch_lower:
                choices += 1
                continue
            if present(p_arm, rk) or hit["history"] or rk[2].lower() == rk[3].lower():
                continue
            if is_base_game(rk[2], words) or is_base_game(rk[3], words):
                continue
            if (rk[0], p_of(rk[0])) in measured:
                problems.append(rk)
                problem_finding.setdefault(rk[0], []).append(hit["finding"].get("id"))
        spot_rows = [e for e in record if e["admission"] not in ("admitted", "repeat")
                     and (e["checkId"], patched_arm) in measured and not present(patched_arm, keyof(e))]
        spots = collections.Counter(e["admission"] for e in spot_rows)
        blind = [{"admission": k, "count": n, "why": words["blindSpotWhy"][k]} for k, n in sorted(spots.items())]
        # overwrite history is not a predicted clash, but a row the patch removes whose physical test held (the
        # overwritten mod's ground lost) is named with its count (Winterhold review 8; the claim stays demoted)
        history_holds = sum(1 for e in spot_rows if e["admission"] == "history" and e.get("physical") == "Holds")
        sentences = [bind(words["validationHits"], {"patch": patch, "count": len(hits)})]
        if kept:
            sentences.append(bind(words["validationKept"], {"patch": patch, "count": len(kept)}))
        sentences += [bind(words["validationOpen"], {"patch": patch, "count": len(open_)}),
                     bind(words["validationPatchProblems"], {"patch": patch, "count": len(problems)})]
        sentences += [bind(words["validationBlindSpot"], {"patch": patch, "count": s["count"], "why": s["why"]}) for s in blind]
        if history_holds:
            sentences.append(bind(words["validationHistoryHolds"], {"count": history_holds,
                                                                    "total": spots.get("history", 0)}))
        if untold:
            sentences.append(bind(words["validationCannotTell"], {"patch": patch, "count": len(untold)}))
        if untold_value:
            sentences.append(bind(words["validationValueUnrecorded"], {"patch": patch, "count": len(untold_value)}))
        if not_measured:
            sentences.append(bind(words["validationNotMeasured"], {"patch": patch, "count": len(not_measured)}))
        # the same counts per prediction item, in the items' order, so a reader can be told which items the patch fixes
        def item_of(e):
            return (e["checkId"], e["kind"], tuple(sorted([e["A"], e["B"]], key=str.lower)),
                    e.get("attribution"), e.get("attributionMod"))
        by_item = []
        for g in prediction["items"]:
            k = (g["checkId"], g["kind"], tuple(g["mods"]), g["attribution"], g["attributionMod"])
            by_item.append({"hits": sum(1 for e in hits if item_of(e) == k),
                            "open": sum(1 for e in open_ if item_of(e) == k),
                            "kept": sum(1 for e in kept if item_of(e) == k),
                            "cannotTell": sum(1 for e in untold + untold_value if item_of(e) == k),
                            "notMeasured": sum(1 for e in not_measured if item_of(e) == k)})
        new_by_check = [{"title": title_of(c), "count": sum(1 for rk in problems if rk[0] == c),
                         "findingIds": [x for x in dict.fromkeys(fids) if x]}
                        for c, fids in sorted(problem_finding.items(), key=lambda kv: title_of(kv[0]))]
        ow = words["itemOutcomeWords"]

        def outcome_text(bi):
            parts = [bind(ow[k], {"count": fmt_n(bi.get(k) or 0)})
                     for k in ("hits", "kept", "open", "cannotTell", "notMeasured") if bi.get(k)]
            return ", ".join(parts) if parts else ow["none"]
        item_lines, alone_total = [], collections.Counter()
        for g, bi in zip(prediction["items"], by_item):
            if g.get("shownAs") == "alone":
                alone_total.update({k: v for k, v in bi.items() if isinstance(v, int)})
                alone_total["count"] += g["count"]
                continue
            item_lines.append(bind(words["validationItemLine"], {"title": g["title"], "modA": g["mods"][0],
                                                                 "modB": g["mods"][1], "count": fmt_n(g["count"]),
                                                                 "outcomes": outcome_text(bi)}))
        if alone_total.get("count"):
            item_lines.append(bind(words["validationAloneLine"], {"count": fmt_n(alone_total["count"]),
                                                                  "outcomes": outcome_text(alone_total)}))
        validation = {"status": "compared", "headline": ctx["oldVerdict"], "patches": patch_names,
                      "itemLines": item_lines, "newByCheck": new_by_check,
                      "newByCheckHeading": words["validationNewByCheck"],
                      "withPatchArm": patched_arm, "withoutPatchArm": p_arm, "byItem": by_item,
                      "hits": len(hits), "kept": len(kept), "open": len(open_),
                      "cannotTell": len(untold) + len(untold_value), "valueUnrecorded": len(untold_value),
                      "falseAlarms": None,
                      "patchProblems": len(problems),
                      "patchChoices": choices,
                      "notMeasured": len(not_measured), "blindSpots": blind, "sentences": sentences,
                      "otherComparisons": [c for c in comparisons if c["withPatch"] != patched_arm or c["withoutPatch"] != p_arm],
                      "missingFacts": list(words["validationMissingFacts"])}
    headings = {k: bind(v, {"subject": subject}) for k, v in words["headings"].items()}
    return {"prediction": prediction, "coverage": coverage, "validation": validation, "headings": headings}


def compile_plan(bundle_dir: Path, verify: dict, settings: dict, pr: dict, bundle_dir_as_given: str,
                 location_scopes: dict | None = None, sibling_dir: Path | None = None,
                 sibling_as_given: str | None = None) -> dict:
    """sibling_dir: a verified bundle of the same order without the patch; its single-mod setups stand in for the ones
    this bundle lacks (build_prediction's join). Raises InputRefused when it cannot be joined at all."""
    b = Bundle(bundle_dir)
    sibling = None
    if sibling_dir is not None:
        sib_plan = compile_plan(Path(sibling_dir), verify, settings, pr, str(sibling_dir))
        sibling = {"bundle": Bundle(Path(sibling_dir)), "findings": sib_plan.get("findings") or [],
                   "planId": Bundle(Path(sibling_dir)).plan.get("planId"),
                   "manifestSha256": sha256_file(Path(sibling_dir) / "manifest.json"),
                   "pathAsGiven": sibling_as_given or str(sibling_dir)}
    disp = b.disposition
    ev = disp.get("evaluation") or {}
    arms_disp = disp.get("arms") or {}
    plan_arms = {a.get("armId"): a for a in (b.plan.get("arms") or []) if isinstance(a, dict)}
    kinds = {a: (v.get("kind") or plan_arms.get(a, {}).get("kind")) for a, v in arms_disp.items()}
    for a, v in plan_arms.items():
        kinds.setdefault(a, v.get("kind"))
    arm_ids = sorted(set(arms_disp) | set(plan_arms), key=lambda a: arm_sort_key(a, kinds))

    # --- what each alternative setup changes in the measured load order (the baseline arm's requested plugins).
    #     The patch is whatever non-target plugin distinguishes an alternative from the baseline, in EITHER
    #     direction: a candidate may add it, or the baseline may carry it and the candidate remove it. A comparison
    #     sentence names that change, and a setup that removes a plugin is never called the supplied setup.
    base_names = [p.get("name") for p in ((plan_arms.get("baseline") or {}).get("requested") or {}).get("plugins") or []]
    base_set = {n.lower() for n in base_names if n}
    arm_changes, arm_orders = {}, {}
    for arm_id in arm_ids:
        names = [p.get("name") for p in ((plan_arms.get(arm_id) or {}).get("requested") or {}).get("plugins") or []]
        arm_orders[arm_id] = [n for n in names if n]
        added = [n for n in names if n and n.lower() not in base_set] if arm_id != "baseline" else []
        removed = [n for n in base_names if n and n.lower() not in {x.lower() for x in names if x}] if (arm_id != "baseline" and names) else []
        arm_changes[arm_id] = (added, removed)
    # SW2: a plugin is the same plugin only with the same NAME and the same BYTES. An arm can keep every name and still
    # load a different file under one of them (an author's same-named "compatible" variant). A row whose hash is
    # unstated on either side cannot be compared, and is said to be so rather than taken as equal.
    base_rows = {str(p.get("name")).lower(): p for p in
                 ((plan_arms.get("baseline") or {}).get("requested") or {}).get("plugins") or [] if p.get("name")}

    def stated(row):
        sha = str((row or {}).get("sha256") or "")
        return sha.lower() if len(sha) == 64 and (row or {}).get("hashSource") != "unstated" else None
    arm_replaced, arm_unverified = {}, {}
    for arm_id in arm_ids:
        replaced, unverified = [], []
        if arm_id != "baseline":
            for p in ((plan_arms.get(arm_id) or {}).get("requested") or {}).get("plugins") or []:
                b_row = base_rows.get(str(p.get("name") or "").lower())
                if b_row is None:
                    continue
                mine, theirs = stated(p), stated(b_row)
                if mine is None or theirs is None:
                    unverified.append(p.get("name"))
                elif mine != theirs:
                    replaced.append(p.get("name"))
        arm_replaced[arm_id], arm_unverified[arm_id] = replaced, unverified

    def replacement_folder(arm_id, name):
        row = next((p for p in ((plan_arms.get(arm_id) or {}).get("requested") or {}).get("plugins") or []
                    if str(p.get("name") or "").lower() == str(name).lower()), {})
        src = str(row.get("hashSource") or "")
        return src.split(":", 1)[1] if src.startswith("instance:") else None
    base_order = [n.lower() for n in arm_orders.get("baseline") or []]
    plan_target_set = {str(t.get("name")).lower() for t in (b.plan.get("targets") or []) if isinstance(t, dict) and t.get("name")}

    # --- WHAT KIND of comparison each alternative arm is.
    #
    # This distinction is load-bearing and its absence produced two wrong statements. One
    # concept -- "the plugin that differs from baseline" -- was doing three incompatible
    # jobs, so an arm that merely REMOVES content was eligible to be described as resolving
    # conflicts, and any non-target plugin that happened to differ was named as a patch.
    #
    #   patch-comparison    the arm ADDS a non-target plugin. Only this kind can support a
    #                       repair reading, because only here does the alternative contain
    #                       something the reader could install.
    #   dependency-control  the arm removes every target, leaving the shared dependencies. A
    #                       finding surviving here is inherited, not created by the targets.
    #   mod-removal         the arm removes some but not all targets. Fewer conflicts here is
    #                       arithmetic: a setup without a mod does not have that mod's
    #                       conflicts.
    #   same-order-control  the arm's plugins equal the baseline's -- names, order AND bytes --
    #                       a comparability control, not a change.
    #   same-name-replacement  the same names in the same order, but at least one is a different
    #                       file (stated hashes differ): an author's same-named variant, say.
    #   bytes-not-compared  the same names in the same order, but a hash is unstated on one side,
    #                       so whether it is the same file was not checked; never called a control.
    #   mixed               adds and removes at once; described explicitly, never summarised.
    # --- DECLARED patch identity. A patch is declared, never inferred.
    #
    # Inferring it from plugin membership was wrong in both directions: adding any unrelated
    # plugin was read as a patch comparison, and nothing distinguished a patch from a plugin
    # that merely differs. Plugin membership does not identify a patch. Sources, in order:
    # the plan's own `patches` list, an arm's `provenance.patch`/`patches`/`patchPlugins`, and
    # `--declare-patch` at report time for a plan that predates the field. Absent all three,
    # the difference is reported as UNSTATED rather than named as a patch.
    declared_patches = set()
    for v in (b.plan.get("patches") or []):
        name = v if isinstance(v, str) else (v or {}).get("name")
        if name:
            declared_patches.add(str(name).lower())
    for a in plan_arms.values():
        prov = (a or {}).get("provenance") or {}
        for key in ("patch", "patches", "patchPlugins"):
            v = prov.get(key)
            for name in ([v] if isinstance(v, str) else (v or [])):
                if isinstance(name, str) and name.strip():
                    declared_patches.add(name.strip().lower())
    for name in (settings.get("declaredPatches") or []):
        if isinstance(name, str) and name.strip():
            declared_patches.add(name.strip().lower())

    def arm_comparison_kind(arm_id) -> str:
        if arm_id == "baseline":
            return "baseline"
        added, removed = arm_changes.get(arm_id) or ([], [])
        removed_targets = [n for n in removed if n.lower() in plan_target_set]
        added_targets = [n for n in added if n.lower() in plan_target_set]
        replaced = arm_replaced.get(arm_id) or []
        same_order = [n.lower() for n in arm_orders.get(arm_id) or []] == base_order
        if added and removed:
            return "mixed"
        if not added and not removed:
            # Same MEMBERSHIP is not the same configuration. A load order is ordered, and
            # reordering it is a real tested alternative that can change every winner.
            # Calling it a control asserted an equality that was never checked. Nor is the same
            # NAME the same file (SW2): a same-named replacement is its own comparison.
            if replaced:
                return "same-name-replacement" if same_order else "mixed"
            if not same_order:
                return "reordered-alternative"
            return "bytes-not-compared" if arm_unverified.get(arm_id) else "same-order-control"
        if replaced:
            moved = added + removed + replaced
            if any(n.lower() in plan_target_set for n in moved):
                return "mixed"
            return ("patch-comparison" if all(n.lower() in declared_patches for n in moved)
                    else "unstated-difference")
        # An evaluated MOD moving decides the kind, whichever direction it moves in. Its
        # companion files travel with it and are collateral, not a patch: a mod's own extra
        # masters differ from the baseline for the same reason the mod does.
        if removed_targets or added_targets:
            if removed_targets and not added:
                return ("dependency-control"
                        if plan_target_set and len(removed_targets) >= len(plan_target_set)
                        else "mod-removal")
            return "mixed"
        # Only NON-target plugins move. That is a patch comparison in either direction ONLY
        # if every plugin that moved is a DECLARED patch. Otherwise the identity is unstated
        # and is reported as such.
        moved = [n for n in added + removed]
        if moved and all(n.lower() in declared_patches for n in moved):
            return "patch-comparison"
        return "unstated-difference"

    comparison_kinds = {a: arm_comparison_kind(a) for a in arm_ids}

    # A plugin is "a patch evaluated" only when it is the difference in an arm that is a
    # PATCH COMPARISON. Previously any non-target plugin that differed at all was named as a
    # patch, so a mod's own companion masters -- removed only because the mod they belong to
    # was removed -- were reported to the reader as the patches under evaluation.
    load_position = {n.lower(): i for i, n in enumerate(arm_orders.get("baseline") or [])}
    for arm_id in arm_ids:
        for i, n in enumerate(arm_orders.get(arm_id) or []):
            load_position.setdefault(n.lower(), len(load_position) + i)
    supplied = sorted({n
                       for arm_id, (added, removed) in arm_changes.items()
                       if comparison_kinds.get(arm_id) == "patch-comparison"
                       for n in added + removed
                       if n.lower() in declared_patches and n.lower() not in plan_target_set},
                      key=lambda n: (load_position.get(n.lower(), 10 ** 6), n.lower()))
    # Differences carried by an arm whose patch identity is not declared. Named to the reader
    # as what they are -- a plugin that differs -- and never as an evaluated patch.
    unstated_difference_plugins = sorted({n
                                          for arm_id, (added, removed) in arm_changes.items()
                                          if comparison_kinds.get(arm_id) == "unstated-difference"
                                          for n in added + removed})
    # Declared patches changed by a MIXED arm (one that adds and removes at once). Such an arm
    # evaluates no patch on its own, but the reader must not be told no patch was declared.
    mixed_declared_patches = sorted({n
                                     for arm_id, (added, removed) in arm_changes.items()
                                     if comparison_kinds.get(arm_id) == "mixed"
                                     for n in added + removed
                                     if n.lower() in declared_patches and n.lower() not in plan_target_set
                                     and n not in supplied})

    def repair_claimable(ids) -> bool:
        """Can a reduction across these arms be read as REPAIR the reader could install?

        Only from an arm that is a DECLARED patch comparison. A reduction from removing a mod
        is arithmetic; a reduction from reordering is a load-order change, not a patch; and a
        reduction from an undeclared plugin cannot be attributed to a patch at all, because
        nothing establishes that the plugin is one.
        """
        return any(comparison_kinds.get(a) == "patch-comparison" for a in ids)

    def non_repair_reason(ids) -> str:
        kinds_here = {comparison_kinds.get(a) for a in ids}
        if kinds_here <= {"dependency-control"}:
            return ("These setups remove every evaluated mod, so what remains is the shared "
                    "dependencies. Fewer conflicts here does not describe a fix; it separates "
                    "what the dependencies already carried from what the mods add.")
        if kinds_here <= {"reordered-alternative"}:
            return ("These setups hold the same plugins in a different load order. A change "
                    "here is a load-order effect, not a patch, and installing nothing would "
                    "reproduce it.")
        if kinds_here <= {"same-name-replacement"}:
            return ("These setups swap a plugin for a different file with the same name, such as an "
                    "author's compatibility version. A change here comes from that file's contents; "
                    "it is not reported as a fix.")
        if kinds_here <= {"bytes-not-compared"}:
            return ("These setups list the same plugins in the same order, but the plan could not state "
                    "the bytes of every one, so they were not compared and the setups are not called "
                    "the same.")
        if kinds_here <= {"unstated-difference"}:
            return ("These setups differ by a plugin that is not declared as a patch, so the "
                    "change cannot be attributed to one. Declare the patch to have this "
                    "reported as a patch comparison.")
        return ("These setups reach that by removing content rather than by patching it: a "
                "setup without a mod does not have that mod's conflicts. They are controls "
                "for attribution, not a configuration to install.")


    def arm_subject(arm_id) -> str:
        added, removed = arm_changes.get(arm_id) or ([], [])
        replaced = [f"replacing {n} with another file of the same name"
                    + (f" (from {replacement_folder(arm_id, n)})" if replacement_folder(arm_id, n) else "")
                    for n in arm_replaced.get(arm_id) or []]
        unverified = ([f"keeping {', '.join(arm_unverified[arm_id])}, whose bytes were not compared"]
                      if not added and not removed and not replaced and arm_unverified.get(arm_id) else [])
        def named(ns):
            # more than two declared patches are "the N patches" here; the setups list names every one (P53)
            if len(ns) > 2 and all(n.lower() in declared_patches for n in ns):
                return f"the {len(ns)} patches"
            return ", ".join(ns)
        change = " and ".join(([f"adding {named(added)}"] if added else []) + ([f"removing {named(removed)}"] if removed else [])
                              + replaced + unverified)
        return change[:1].upper() + change[1:] if change else "The supplied setup"

    def setup_subject(ids) -> str:
        subjects = {arm_subject(a) for a in ids}
        if len(subjects) == 1:
            return subjects.pop()
        return "A tested alternative setup" if any((arm_changes.get(a) or ([], []))[1] for a in ids) else "The supplied setup"

    def lower_first(s: str) -> str:
        return s[:1].lower() + s[1:]

    def subject_values(ids) -> dict:
        # the skill's wording templates take the same subject as {subject} (sentence start) / {subjectLower}
        subject = setup_subject(ids)
        return {"subject": subject, "subjectLower": lower_first(subject)}

    templates = pr.get("templates") or {}
    reader_data = pr.get("reader") or {}
    reader_comparison_templates = reader_data.get("comparisonTemplates") or {}
    reader_verdict_templates = reader_data.get("verdictTemplates") or {}
    overlap_terms = (reader_data.get("kindPhrases") or {}).get("OVERLAP") or {}

    def overlap_candidate_noun(count: int) -> str:
        return (overlap_terms["candidateSingular"] if count == 1 else overlap_terms["candidatePlural"])

    # --- evidence rows: E-<manifest id>, with the receipt's part attached where the manifest lacks it
    evidence = []
    eid_by_path = {}
    for e in b.evidence:
        rid = e.get("recordedBy") or ((e.get("provenance") or {}).get("receiptId"))
        rc = b.receipts.get(rid) if rid else None
        row = {"id": f"E-{e.get('id')}", "kind": e.get("kind"), "path": e.get("path"), "sha256": e.get("sha256"),
               "bytes": e.get("bytes"), "checkId": e.get("checkId") or (rc or {}).get("checkId"),
               "armId": e.get("armId") or (rc or {}).get("armId"), "part": (rc or {}).get("part"),
               "strength": e.get("strength"), "recordedBy": e.get("recordedBy")}
        evidence.append(row)
        eid_by_path[e.get("path")] = row["id"]
    evidence.sort(key=lambda r: (r["path"] or ""))

    def eids_for(check_id, arm_id, part) -> list[str]:
        ids = []
        for e in b.evidence_for(check_id, arm_id, part):
            i = f"E-{e.get('id')}"
            if i not in ids:
                ids.append(i)
        return ids

    # --- media rows (manifest rows whose strength is supplied-media) and unassessed media
    media = []
    for e in b.evidence:
        if e.get("strength") != "supplied-media":
            continue
        sha = e.get("sha256") or ""
        media.append({"id": f"M-{sha[:12]}", "manifestId": e.get("id"), "path": e.get("path"), "sha256": sha,
                      "bytes": e.get("bytes"), "mediaKind": e.get("mediaKind"), "subject": e.get("subject"),
                      "state": e.get("state"), "configurationFingerprint": e.get("configurationFingerprint"),
                      "fingerprintSource": e.get("fingerprintSource"), "provenance": e.get("provenance"),
                      "sourceInterval": e.get("sourceInterval"), "original": e.get("original"),
                      "transformation": e.get("transformation"), "assessed": True, "reason": None})
    unassessed_media = []
    for u in b.manifest.get("unassessedMedia") or []:
        if not isinstance(u, dict):
            continue
        item = u.get("item") if isinstance(u.get("item"), dict) else u
        sha = u.get("sha256") or item.get("sha256") or sha256_text(canonical(u))
        unassessed_media.append({"id": f"M-{sha[:12]}", "manifestId": None, "path": item.get("path") or u.get("path"),
                                 "sha256": u.get("sha256") or item.get("sha256"), "mediaKind": item.get("mediaKind"),
                                 "subject": item.get("subject"), "state": item.get("state"),
                                 "configurationFingerprint": item.get("configurationFingerprint"),
                                 "fingerprintSource": item.get("fingerprintSource"), "provenance": item.get("provenance"),
                                 "sourceInterval": item.get("sourceInterval"), "original": item.get("original"),
                                 "transformation": item.get("transformation"), "assessed": False,
                                 "reason": u.get("reason") or "reason not stated"})
    media.sort(key=lambda m: (m["path"] or ""))
    unassessed_media.sort(key=lambda m: (m["path"] or ""))

    def media_for(check_id, arm_id, form_keys, part=None) -> list[str]:
        """A media item declared for arm X illustrates X's OWN configuration: it binds to X's blocks with part
        null, arm or diff, never to X's reverse part (the baseline order measured with X as --base)."""
        out = []
        if part == "reverse":
            return out
        for m in media:
            s = m.get("subject") or {}
            if s.get("checkId") != check_id or s.get("armId") != arm_id:
                continue
            fk = s.get("formKey")
            if fk and form_keys and fk not in form_keys:
                continue
            out.append(m["id"])
        return out

    # --- groups
    groups: dict[tuple, dict] = {}

    def group_for(check_id, kind, a_, b_) -> dict:
        key = (check_id, kind, a_, b_)
        if key not in groups:
            groups[key] = {"id": None, "title": f"{kind}: {a_} x {b_}", "checkId": check_id,
                           "key": f"{kind}|{a_}|{b_}", "findingIds": [], "rowsTotal": 0}
        return groups[key]

    findings: list[dict] = []
    mode = settings["mode"]
    rows_per_group = settings["rowsPerGroup"]
    reverse_rule = (pr.get("concise") or {}).get("reversePartRows", "index-only")
    context_only_by_check = ((pr.get("reader") or {}).get("contextOnlyKinds") or {})

    def context_only(check_id, group_kind) -> bool:
        return str(group_kind or "") in set(context_only_by_check.get(str(check_id), []) or [])

    def add(f: dict) -> dict:
        f.setdefault("rows", [])
        f["rowsTotal"] = len(f["rows"])
        f.setdefault("mediaIds", [])
        f.setdefault("evidenceIds", [])
        f.setdefault("groupId", None)
        findings.append(f)
        return f

    # What each comparison's own record says about it, read once: a verdict the comparison refused (an UNJUDGED
    # verdict, its log's `REFUSED:` line) prints no repaired or created count anywhere; counts the controller did not
    # accept (a `missing` diff row whose comparison was not refused: rows measured on one side only) are printed with
    # that qualifier; rows that left the class with their defect still there are named beside a no-problem line.
    missing_diff = {(m.get("checkId"), m.get("armId")) for m in ev.get("missing") or [] if m.get("part") == "diff"}
    comparison_state = {}
    for arm_id in arm_ids:
        per = (((arms_disp.get(arm_id) or {}).get("orderEvaluation") or {}).get("perProbe") or {})
        for probe in sorted(per):
            check_id = b.exe_to_check.get(probe, probe)
            log_path = b.diff_log_path(check_id, arm_id)
            text = read_text(bundle_dir / log_path) if log_path else ""
            refused_line = next((m.group(1).strip() for m in re.finditer(r"^\s*!\s*REFUSED:\s*(.+)$", text, re.M)), None)
            holding = re.search(r"left the class still holding (\d+)", text)
            verdict_ = str((per[probe] or {}).get("verdict") or "")
            refused = verdict_.endswith("UNJUDGED") or refused_line is not None
            comparison_state[(check_id, arm_id)] = {
                "verdict": verdict_, "refused": refused, "refusedWhy": refused_line,
                "notAccepted": (check_id, arm_id) in missing_diff and not refused,
                "unmeasured": int((per[probe] or {}).get("unmeasured") or 0),
                "stillHolding": int(holding.group(1)) if holding else 0}

    # (1) regressions and (4) repairs, from orderEvaluation verbatim
    for arm_id in arm_ids:
        oe = (arms_disp.get(arm_id) or {}).get("orderEvaluation") or {}
        per = oe.get("perProbe") or {}
        for probe in sorted(per):
            p = per[probe] or {}
            check_id = b.exe_to_check.get(probe, probe)
            counts = {"probe": probe, "classification": oe.get("classification"), **{k: p.get(k) for k in
                      ("verdict", "repaired", "reclassified", "created", "newDefect", "attributed", "unmeasured")}}
            log_path = b.diff_log_path(check_id, arm_id)
            diff_lines = parse_diff_log(read_text(bundle_dir / log_path)) if log_path else []
            disp_row = next((r for r in ev.get("findings") or [] if r.get("checkId") == check_id and
                             r.get("armId") == arm_id and r.get("part") == "diff"), None)
            strength = (disp_row or {}).get("strength") or (b.checks.get(check_id, {}).get("evidence") or {}).get("strength")
            emit_path = b.output_emit_path(check_id, arm_id, "arm")
            emit = b.emit(emit_path) if emit_path else None
            if (p.get("newDefect") or 0) > 0:
                created_groups = [d for d in diff_lines if d["change"] in ("NEW", "GREW") and d.get("created", 0) > 0]
                shrank_groups = [d for d in diff_lines if d["change"] in ("SHRANK", "GONE")]
                rows = []
                reader_form_keys = set()
                group = None
                for d in created_groups:
                    g = group_for(check_id, d["kind"], d["A"], d["B"])
                    group = group or g
                    if isinstance(emit, dict):
                        for c in emit.get("Conflicts") or []:
                            if (c.get("Kind"), c.get("A"), c.get("B")) == (d["kind"], d["A"], d["B"]):
                                group_rows = conflict_rows(c, eid_by_path.get(emit_path))
                                rows.extend(group_rows)
                                if not context_only(check_id, d.get("kind")):
                                    reader_form_keys.update(x.get("formKey") for x in group_rows if x.get("formKey"))
                first = created_groups[0] if created_groups else {}
                gated = bool(context_only_by_check.get(str(check_id)))
                reader_groups = [d for d in created_groups if not context_only(check_id, d.get("kind"))]
                reader_shrank = [d for d in shrank_groups if not context_only(check_id, d.get("kind"))]
                reader_created = (sum(int(d.get("created") or 0) for d in reader_groups) if created_groups
                                  else (0 if gated else int(p.get("newDefect") or p.get("created") or 0)))
                reader_repaired = (sum(int(d.get("repaired") or 0) for d in reader_shrank) if shrank_groups
                                   else (0 if gated else int(p.get("repaired") or 0)))
                counts_r = {**counts, "groups": [{"kind": d["kind"], "A": d["A"], "B": d["B"], "created": d.get("created", 0)}
                                                 for d in created_groups], "readerCreated": reader_created,
                            "readerRepaired": reader_repaired}
                f = add({"id": None, "kind": "regression", "priority": 1, "checkId": check_id,
                         "family": b.check_family(check_id), "armId": arm_id, "part": "diff",
                         "title": b.check_title(check_id), "strength": strength, "verdict": p.get("verdict"),
                         "plain": bind(templates.get("regression", ""), {**counts, "armId": arm_id,
                                                                            "groupKind": first.get("kind", "conflict")}),
                         "counts": {**counts_r, "diffLines": [d["line"] for d in created_groups]},
                         "evidenceIds": eids_for(check_id, arm_id, "diff") + [x for x in eids_for(check_id, arm_id, "arm")
                                                                             if x not in eids_for(check_id, arm_id, "diff")],
                         "mediaIds": media_for(check_id, arm_id, reader_form_keys),
                         "groupId": group, "rows": rows})
                f["readerRelevant"] = bool(reader_groups) if created_groups else not gated
                if not f["readerRelevant"] and gated:
                    f["readerEvidenceReason"] = "context-only-or-inseparable"
                f["_group"] = group
            if (p.get("repaired") or 0) > 0 or (p.get("reclassified") or 0) > 0:
                kind = "repaired" if (p.get("repaired") or 0) > 0 else "reclassified"
                shrank = [d for d in diff_lines if d["change"] in ("SHRANK", "GONE")]
                reader_shrank = [d for d in shrank if not context_only(check_id, d.get("kind"))]
                gated = bool(context_only_by_check.get(str(check_id)))
                reader_repaired = (sum(int(d.get("repaired") or 0) for d in reader_shrank) if shrank
                                   else (0 if gated else int(p.get("repaired") or 0)))
                reader_reclassified = (sum(int(d.get("reclassified") or 0) for d in reader_shrank) if shrank
                                       else (0 if gated else int(p.get("reclassified") or 0)))
                f = add({"id": None, "kind": kind, "priority": 4, "checkId": check_id,
                         "family": b.check_family(check_id), "armId": arm_id, "part": "diff",
                         "title": b.check_title(check_id), "strength": strength, "verdict": p.get("verdict"),
                         "plain": bind(templates.get(kind, ""), {**counts, "armId": arm_id}),
                         "counts": {**counts, "diffLines": [d["line"] for d in shrank],
                                    "readerRepaired": reader_repaired, "readerReclassified": reader_reclassified},
                         "evidenceIds": eids_for(check_id, arm_id, "diff"), "rows": []})
                f["readerRelevant"] = bool(reader_shrank) if shrank else not gated
                if not f["readerRelevant"] and gated:
                    f["readerEvidenceReason"] = "context-only-or-inseparable"

    # (2) missing, verbatim from the disposition
    for m in ev.get("missing") or []:
        check_id = m.get("checkId")
        add({"id": None, "kind": "missing", "priority": 2, "checkId": check_id, "family": m.get("family") or b.check_family(check_id),
             "armId": m.get("armId"), "part": m.get("part"), "title": b.check_title(check_id), "strength": None,
             "verdict": None, "plain": bind(templates.get("missing", ""), {"armId": m.get("armId"), "status": m.get("status"),
                                                                             "cause": m.get("cause") or ""}),
             "counts": {"status": m.get("status"), "cause": m.get("cause"), "required": m.get("required")},
             "evidenceIds": eids_for(check_id, m.get("armId"), m.get("part")), "rows": []})

    # (3) findings on each arm: one per conflict group per (check, arm, part); a diff part with findings
    #     and no regression row; a non-conflict-file finding as one count
    regression_keys = {(f["checkId"], f["armId"]) for f in findings if f["kind"] == "regression"}
    for r in sorted(ev.get("findings") or [], key=lambda r: (r.get("checkId") or "", arm_sort_key(r.get("armId") or "", kinds),
                                                             PART_ORDER.get(r.get("part"), 9))):
        check_id, arm_id, part = r.get("checkId"), r.get("armId"), r.get("part")
        oe = ((arms_disp.get(arm_id) or {}).get("orderEvaluation") or {}).get("perProbe") or {}
        exe = (b.checks.get(check_id, {}).get("adapter") or {}).get("exe")
        verdict = r.get("verdict") or (oe.get(exe) or {}).get("verdict")
        base_eids = eids_for(check_id, arm_id, part)
        if part == "diff":
            if (check_id, arm_id) in regression_keys:
                continue
            p = oe.get(exe) or {}
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                 "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                 "verdict": verdict, "plain": bind(templates.get("finding-diff", ""), {**p, "probe": exe, "armId": arm_id}),
                 "counts": {"findings": r.get("findings"), **p}, "evidenceIds": base_eids, "rows": []})
            if context_only_by_check.get(str(check_id)):
                f["readerRelevant"] = False  # aggregate diff cannot honestly separate context-only kinds
                f["readerEvidenceReason"] = "context-only-or-inseparable"
            continue
        emit_path = b.output_emit_path(check_id, arm_id, part)
        emit = b.emit(emit_path) if emit_path else None
        conflicts = emit.get("Conflicts") if isinstance(emit, dict) else None
        if isinstance(conflicts, list) and not conflicts and r.get("findings"):
            # rows derived from a counted result that hold no row of their own: measured, nothing between mods
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                     "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                     "verdict": verdict, "plain": bind(templates.get("finding-count", ""), {
                         "findings": r.get("findings"), "armId": arm_id, "strength": r.get("strength")}),
                     "counts": {"findings": r.get("findings"), "derivedRows": 0}, "evidenceIds": base_eids, "rows": []})
            f["readerRelevant"] = False
            f["readerEvidenceReason"] = "derived-rows-none"
            continue
        if not isinstance(conflicts, list):
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                 "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                 "verdict": verdict, "plain": bind(templates.get("finding-count", ""), {"findings": r.get("findings"),
                                                                                       "armId": arm_id, "strength": r.get("strength")}),
                 "counts": {"findings": r.get("findings")}, "evidenceIds": base_eids, "rows": []})
            if context_only_by_check.get(str(check_id)):
                f["readerRelevant"] = False  # count-only result cannot identify which kinds are reader findings
                f["readerEvidenceReason"] = "context-only-or-inseparable"
            continue
        by_key: dict[tuple, list] = collections.OrderedDict()
        for c in conflicts:
            if not isinstance(c, dict):
                continue
            by_key.setdefault((c.get("Kind"), c.get("A"), c.get("B")), []).append(c)
        coverage = [{"population": c.get("Population"), "judged": c.get("Judged"), "unjudgeable": c.get("Unjudgeable"),
                     "setAside": c.get("SetAside"), "note": c.get("Note")}
                    for c in (emit.get("Coverage") or []) if isinstance(c, dict)]
        ordered = sorted(by_key.items(), key=lambda kv: (-sum(len(c.get("Necessary") or []) or 1 for c in kv[1]),
                                                         kv[0][1] or "", kv[0][2] or "", kv[0][0] or ""))
        for (kind, a_, b_), cs in ordered:
            rows = []
            for c in cs:
                rows.extend(conflict_rows(c, eid_by_path.get(emit_path)))
            rows.sort(key=lambda x: (x.get("formKey") or "", x.get("summary") or ""))
            g = group_for(check_id, kind, a_, b_)
            f = add({"id": None, "kind": "finding", "priority": 3, "checkId": check_id, "family": b.check_family(check_id),
                     "armId": arm_id, "part": part, "title": b.check_title(check_id), "strength": r.get("strength"),
                     "verdict": verdict,
                     "plain": bind(templates.get("finding", ""), {"rowsTotal": len(rows), "groupKind": kind, "A": a_, "B": b_,
                                                                  "armId": arm_id, "part": part or "arm",
                                                                  "findingsInPart": r.get("findings"), "strength": r.get("strength")}),
                     "counts": {"groupKind": kind, "A": a_, "B": b_, "conflicts": len(cs), "rows": len(rows),
                                "findingsInPart": r.get("findings"), "coverage": coverage,
                                "population": emit.get("Population"), "state": emit.get("State")},
                     "evidenceIds": base_eids, "mediaIds": media_for(check_id, arm_id, {x["formKey"] for x in rows}, part),
                     "rows": rows})
            f["readerRelevant"] = not context_only(check_id, kind)
            if not f["readerRelevant"]:
                f["readerEvidenceReason"] = "context-only-kind"
            f["_group"] = g

    # (5) completed null checks with their coverage lines from the receipt
    for r in ev.get("completedNullChecks") or []:
        check_id, arm_id, part = r.get("checkId"), r.get("armId"), r.get("part")
        rc = b.receipt_by_key.get((check_id, arm_id, part)) or {}
        cov = [{"population": c.get("Population"), "judged": c.get("Judged"), "unjudgeable": c.get("Unjudgeable"),
                "setAside": c.get("SetAside"), "note": c.get("Note")}
               for c in ((rc.get("result") or {}).get("coverage") or []) if isinstance(c, dict)]
        add({"id": None, "kind": "null", "priority": 5, "checkId": check_id, "family": b.check_family(check_id),
             "armId": arm_id, "part": part, "title": b.check_title(check_id),
             "strength": (rc.get("result") or {}).get("strength"), "verdict": (rc.get("result") or {}).get("verdict"),
             "plain": bind(templates.get("null", ""), {"armId": arm_id, "partText": f" ({part} part)" if part else "",
                                                      "coverage": r.get("coverage")}),
             "counts": {"coverage": r.get("coverage"), "coverageLines": cov}, "evidenceIds": eids_for(check_id, arm_id, part),
             "rows": []})

    # (6) catalogues
    for r in ev.get("completedCatalogues") or []:
        check_id, arm_id = r.get("checkId"), r.get("armId")
        add({"id": None, "kind": "catalogue", "priority": 6, "checkId": check_id, "family": b.check_family(check_id),
             "armId": arm_id, "part": r.get("part"), "title": b.check_title(check_id),
             "strength": (b.checks.get(check_id, {}).get("evidence") or {}).get("strength"), "verdict": None,
             "plain": bind(templates.get("catalogue", ""), {"count": r.get("count"), "unit": r.get("unit"), "armId": arm_id}),
             "counts": {"count": r.get("count"), "unit": r.get("unit"), "resultKind": r.get("kind")},
             "evidenceIds": eids_for(check_id, arm_id, r.get("part")), "rows": []})

    # (7) media not assessed, (8) everything under unassessed
    un = disp.get("unassessed") or {}
    for m in unassessed_media:
        add({"id": None, "kind": "unassessed", "area": "media", "priority": 7, "checkId": (m.get("subject") or {}).get("checkId"),
             "family": "runtime", "armId": (m.get("subject") or {}).get("armId"), "part": None, "title": "Media not assessed",
             "strength": None, "verdict": None,
             "plain": bind(templates.get("unassessed-media", ""), {"basename": Path(str(m.get("path") or "")).name or "unnamed",
                                                                   "mediaId": m["id"], "mediaKind": m.get("mediaKind"),
                                                                   "armId": (m.get("subject") or {}).get("armId"), "reason": m.get("reason")}),
             "counts": {"reason": m.get("reason")}, "mediaIds": [m["id"]], "rows": []})
    if not unassessed_media and not media:
        add({"id": None, "kind": "unassessed", "area": "media", "priority": 7, "checkId": None, "family": "runtime",
             "armId": None, "part": None, "title": "Media not assessed", "strength": None, "verdict": None,
             "plain": "No media was supplied to this bundle: no screenshot, render or clip illustrates any finding. "
                      + str(un.get("mediaUnassessed") or ""),
             "counts": {"supplied": 0}, "rows": []})

    def un_add(area, title, text, counts=None, check_id=None, arm_id=None):
        add({"id": None, "kind": "unassessed", "area": area, "priority": 8, "checkId": check_id,
             "family": b.check_family(check_id) if check_id else None, "armId": arm_id, "part": None, "title": title,
             "strength": None, "verdict": None, "plain": bind(templates.get("unassessed", "{text}"), {"text": text}),
             "counts": counts or {}, "evidenceIds": eids_for(check_id, arm_id, None) if check_id and arm_id else [],
             "rows": []})

    states = un.get("statesNotExplored")
    if states:
        un_add("states", "States not explored", "States not explored: " + ", ".join(map(str, states)) + ".",
               {"states": states})
    else:
        un_add("states", "States not explored", "No state beyond those the plan selected was named by the census as "
               "unexplored; the extent of world states is not enumerated by this runner.", {"states": []})
    held = un.get("candidatesHeldBack") or {}
    un_add("candidates", "Candidates held back",
           f"Candidates held back: {fmt_n(held.get('count') or 0)} by --max-candidates; the candidate manifest's own budget "
           f"evaluated {fmt_n((held.get('fromManifestBudget') or {}).get('evaluated') or 0)} and left "
           f"{fmt_n((held.get('fromManifestBudget') or {}).get('legalNotEvaluated') or 0)} legal order(s) not evaluated. "
           + str((held.get("fromManifestBudget") or {}).get("note") or ""), held)
    for u in un.get("unjudgeablePopulations") or []:
        uj = u.get("unjudgeable") or {}
        total = sum(v for v in uj.values() if isinstance(v, (int, float))) if isinstance(uj, dict) else uj
        detail = ", ".join(f"{k} {fmt_n(v)}" for k, v in uj.items()) if isinstance(uj, dict) else str(uj)
        un_add("unjudgeable", "Unjudgeable population",
               bind(templates.get("unjudgeable", ""), {"armId": u.get("armId"), "checkId": u.get("checkId"),
                                                       "judged": u.get("judged"), "population": u.get("population"),
                                                       "unjudgeableTotal": total, "unjudgeableDetail": detail}),
               {"judged": u.get("judged"), "unjudgeable": uj, "population": u.get("population")},
               u.get("checkId"), u.get("armId"))
    fam = un.get("familiesUnsupportedOrManual") or []
    if fam:
        un_add("families", "Families unsupported or manual",
               "Families with checks this runner cannot perform (unsupported, MCP-only or manual): "
               + ", ".join(b.families.get(x, {}).get("title") or x for x in fam) + ".", {"families": fam})
    for s in un.get("scopeUnmeasured") or []:
        un_add("scope", f"Scope extent not measured: {s.get('extent')}",
               f"Not measured ({s.get('extent')}): {s.get('why')}", {"extent": s.get("extent")})
    if un.get("runtimeObservations"):
        un_add("runtime", "Runtime observations", str(un.get("runtimeObservations")), {})

    # --- sort within sections, number findings and groups
    section_of = {}
    for s in pr.get("sections") or []:
        for k in s.get("kinds") or []:
            for area in (s.get("areas") or [None]):
                section_of[(k, area)] = s.get("order", 99)

    def sec(f):
        return section_of.get((f["kind"], f.get("area")), section_of.get((f["kind"], None), 99))

    def count_of(f):
        c = f.get("counts") or {}
        for k in ("newDefect", "created", "rows", "repaired", "reclassified", "findings", "count", "coverage"):
            v = c.get(k)
            if isinstance(v, (int, float)) and not isinstance(v, bool):
                return v
        return f.get("rowsTotal") or 0

    def group_key(f):
        c = f.get("counts") or {}
        return "|".join(str(c.get(k) or "") for k in ("groupKind", "A", "B"))

    findings.sort(key=lambda f: (sec(f), f.get("checkId") or "", arm_sort_key(f.get("armId") or "", kinds), -count_of(f),
                                 (f.get("rows") or [{}])[0].get("formKey") or "", group_key(f),
                                 f.get("counts", {}).get("status") or "", PART_ORDER.get(f.get("part"), 9), f.get("plain") or ""))
    for i, f in enumerate(findings, 1):
        f["id"] = f"F-{i:03d}"
        f["priority"] = sec(f)
        c = f.get("counts") or {}
        labels = (pr.get("reader") or {}).get("kindLabels") or {}
        group_kind = str(c.get("groupKind") or "")
        kind_phrases = (pr.get("reader") or {}).get("kindPhrases") or {}

        def noun(count, singular, plural=None):
            try:
                one = int(count or 0) == 1
            except (TypeError, ValueError):
                one = False
            return singular if one else (plural or singular + "s")

        count_rows = int(f.get("rowsTotal") or 0)
        phrases = kind_phrases.get(group_kind) or {}
        group_label = (phrases.get("singular") if count_rows == 1 else phrases.get("plural")) or labels.get(
            group_kind, noun(count_rows, "reported conflict"))
        if f["kind"] == "regression":
            new_count = int(c.get("readerCreated") or 0)
            repaired_count = int(c.get("readerRepaired") or 0)
            check_phrase = ((pr.get("reader") or {}).get("regressionCheckPhrases") or {}).get(
                f.get("checkId"), "completed check")
            if f.get("checkId") == "geometry.object-overlap":
                f["readerPlain"] = bind(reader_comparison_templates["overlapRegression"],
                                        {"newCount": new_count, **subject_values([f.get("armId")]),
                                         "candidateNoun": overlap_candidate_noun(new_count)})
                if repaired_count:
                    f["readerPlain"] += bind(reader_comparison_templates["overlapRegressionReduction"],
                                             {"repairedCount": repaired_count,
                                              "candidateNoun": overlap_candidate_noun(repaired_count)})
            else:
                f["readerPlain"] = (f"{arm_subject(f.get('armId'))} introduced {fmt_n(new_count)} new "
                                    f"{noun(new_count, 'problem')} in the {check_phrase}." +
                                    (f" It also repaired {fmt_n(repaired_count)} existing {noun(repaired_count, 'problem')}; "
                                     "those repairs do not cancel the new problem." if repaired_count else ""))
        elif f["kind"] == "finding" and c.get("A") and c.get("B"):
            sample = next((r.get("label") for r in f.get("rows") or [] if r.get("label")
                           and " tris" not in str(r.get("label")).lower()
                           and not re.match(r"^[0-9a-f]{6}:", str(r.get("label")), re.I)), None)
            unit = phrases.get("unitSingular") if count_rows == 1 else phrases.get("unitPlural")
            if group_kind == "LOST-EDIT" and str(c.get("B") or "").lower() in declared_patches:
                f["readerPlain"] = bind((pr.get("reader") or {}).get("patchChoiceLine") or "{count} {unit}: {patch}", {
                    "count": fmt_n(count_rows), "unit": unit or "record fields", "patch": c.get("B"), "other": c.get("A")})
                f["readerPatchChoice"] = True
            elif unit:
                f["readerPlain"] = (f"{fmt_n(count_rows)} {unit} {'was' if count_rows == 1 else 'were'} reported between {c.get('A')} and {c.get('B')}. "
                                    f"{phrases.get('readerQualification', '')}".strip())
            else:
                verb = "shows" if count_rows == 1 else "show"
                f["readerPlain"] = (f"{fmt_n(count_rows)} {noun(count_rows, 'record')} {verb} {group_label} between "
                                    f"{c.get('A')} and {c.get('B')}" + (f", including {sample}" if sample else "") + ".")
        elif f["kind"] == "finding":
            finding_count = int(c.get("findings") or c.get("created") or count_rows or 0)
            f["readerPlain"] = (f"This check reported {fmt_n(finding_count)} "
                                f"{noun(finding_count, 'finding')} for this setup. The exact result is in the evidence index.")
        elif f["kind"] in ("repaired", "reclassified"):
            repaired_count = int(c.get("readerRepaired") or 0)
            reclassified_count = int(c.get("readerReclassified") or 0)
            if f.get("checkId") == "geometry.object-overlap":
                clauses = []
                if repaired_count:
                    clauses.append(bind(reader_comparison_templates["overlapReduction"],
                                        {"repairedCount": repaired_count, **subject_values([f.get("armId")]),
                                         "candidateNoun": overlap_candidate_noun(repaired_count)}))
                if reclassified_count:
                    clauses.append(bind(reader_comparison_templates["overlapReclassification"],
                                        {"reclassifiedCount": reclassified_count,
                                         "candidateNoun": overlap_candidate_noun(reclassified_count)}))
                clauses.append(reader_comparison_templates["overlapQualification"])
                f["readerPlain"] = " ".join(clauses)
            else:
                f["readerPlain"] = (f"{arm_subject(f.get('armId'))} repaired {fmt_n(repaired_count)} existing "
                                    f"{noun(repaired_count, 'problem')} and changed which mod owns "
                                    f"{fmt_n(reclassified_count)} still-present {noun(reclassified_count, 'problem')}.")
        elif f["kind"] == "null":
            diff_null = ((pr.get("reader") or {}).get("differentialNullPhrases") or {}).get(f.get("checkId"))
            f["readerPlain"] = (diff_null or "This comparison found no additional problem in the records it examined; "
                                "problems already present in either setup are reported separately."
                                if f.get("part") == "diff" else
                                "This completed check found no problem in the records it examined.")
        elif f["kind"] == "catalogue":
            f["readerPlain"] = f"The check recorded {fmt_n(c.get('count'))} {c.get('unit') or 'item(s)'} for reference."
        else:
            f["readerPlain"] = "Details are recorded in the evidence index."
    cmp_words = reader_data.get("comparisonState") or {}

    def refused_words(state) -> str:
        """A comparison's refusal in words: the share it could not judge, before and after, and the limit."""
        why = str(state.get("refusedWhy") or "")
        m = re.match(r"^(.+?):\s*unjudgeable\s+([\d.]+%)\s*->\s*([\d.]+%).*?more than\s+([\d.]+%)", why)
        if m:
            return bind(cmp_words["refusedShare"], {"population": m.group(1).strip(), "before": m.group(2),
                                                     "after": m.group(3), "limit": m.group(4)})
        return cmp_words["refusedUnnamed"]

    for f in findings:
        state = comparison_state.get((f.get("checkId"), f.get("armId")))
        if not state or f.get("kind") not in ("regression", "repaired", "reclassified"):
            continue
        if state["refused"]:
            # its counts are not a result: never printed as a repair or a new problem, never counted in a verdict
            f["readerRelevant"] = False
            f["readerEvidenceReason"] = "comparison-refused"
        elif state["notAccepted"]:
            f["readerPlain"] = (f.get("readerPlain") or "").rstrip() + " " + bind(
                cmp_words["notAcceptedQualifier"], {"count": state["unmeasured"],
                                                    "rowNoun": "row" if state["unmeasured"] == 1 else "rows"})

    gi = 0
    for key in sorted(groups):
        gi += 1
        groups[key]["id"] = f"G-{gi:03d}"
    for f in findings:
        g = f.pop("_group", None)
        if g:
            f["groupId"] = g["id"]
            g["findingIds"].append(f["id"])
            g["rowsTotal"] += f["rowsTotal"]

    # --- rows shown and omissions (concise), everything (exhaustive). `allRows` is the complete list the
    #     evidence index renders; `rows` is the shown subset, so len(rows) == rowsShown always.
    omissions = []
    for f in findings:
        f["allRows"] = f["rows"]
        if mode == "exhaustive":
            f["rowsShown"] = f["rowsTotal"]
            continue
        if f.get("part") == "reverse" and reverse_rule == "index-only" and f["rowsTotal"] > 0:
            f["rowsShown"] = 0
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'])} reverse-part row(s) not listed in the report",
                              "why": (pr.get("concise") or {}).get("reversePartNote", "index-only"),
                              "where": f"evidence-index.md#{f['id']}"})
            continue
        f["rowsShown"] = min(f["rowsTotal"], rows_per_group)
        if f["rowsShown"] < f["rowsTotal"]:
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'] - f['rowsShown'])} of {fmt_n(f['rowsTotal'])} row(s) not listed in the report",
                              "why": f"concise mode lists {rows_per_group} row(s) per group",
                              "where": f"evidence-index.md#{f['id']}"})
    for f in findings:
        f["rows"] = (f.get("allRows") or [])[: f["rowsShown"]]

    # --- scope
    targets = [{"name": t.get("name"), "sha256": t.get("sha256"), "bytes": t.get("bytes"), "provider": t.get("provider"),
                "providerKind": t.get("providerKind"), "records": (t.get("footprint") or {}).get("records")}
               for t in (b.plan.get("targets") or []) if isinstance(t, dict)]
    target_names_for_scope = [str(t.get("name")) for t in targets if t.get("name")]
    target_set_for_scope = {n.lower() for n in target_names_for_scope}
    evidence_plugins_by_arm = collections.defaultdict(set)
    for f in findings:
        if f.get("part") == "reverse" or f.get("readerRelevant") is False:
            continue
        pair = [str(x) for x in ((f.get("counts") or {}).get("A"), (f.get("counts") or {}).get("B")) if x]
        if any(x.lower() in target_set_for_scope for x in pair):
            evidence_plugins_by_arm[f.get("armId")].update(pair)
    scope_locations = []
    for target in b.plan.get("targets") or []:
        placed = ((target.get("footprint") or {}).get("placed") or {}) if isinstance(target, dict) else {}
        for cell in placed.get("cells") or []:
            if not isinstance(cell, dict):
                continue
            if cell.get("worldspace") and cell.get("x") is not None and cell.get("y") is not None:
                label = f"{cell['worldspace']} ({cell['x']}, {cell['y']})"
            else:
                label = cell.get("name") or cell.get("cell")
            if label and label not in scope_locations:
                scope_locations.append(label)
    arms_out = []
    for arm_id in arm_ids:
        pa = plan_arms.get(arm_id) or {}
        da = arms_disp.get(arm_id) or {}
        req = pa.get("requested") or {}
        names = [p.get("name") for p in req.get("plugins") or []]
        added, removed = arm_changes[arm_id]
        unstated = [{"name": p.get("name"), "unstatedReason": p.get("unstatedReason")}
                    for p in req.get("plugins") or [] if p.get("hashSource") == "unstated"]
        limits = 0
        for rc in b.receipts.values():
            if rc.get("armId") == arm_id and (rc.get("validation") or {}).get("accepted"):
                limits += len((rc.get("validation") or {}).get("identityLimits") or [])
        added_label = ", ".join(added)
        display_label = ("Measured load order" if arm_id == "baseline" else
                         (("Setup including supplied patch" if kinds.get(arm_id) == "supplied-combination" else "Tested alternative setup") +
                          (f": {added_label}" if added_label else "")))
        payload = (da.get("payload") or pa.get("payload") or {})
        setup_limits = []
        if payload.get("espOnly"):
            setup_limits.append("Only the plugin was included; its meshes, scripts and installer choices were not tested.")
        supplied_location_scope = ((location_scopes or {}).get(arm_id) or {}).get("locationContributors") or pa.get("locationContributors") or {}
        supplied_locations = supplied_location_scope.get("locations") or []
        supplied_contributors = [p for loc in supplied_locations if isinstance(loc, dict)
                                  for p in loc.get("plugins") or []]
        selected_plugins = {str(p.get("name")).lower() for p in supplied_contributors
                            if isinstance(p, dict) and p.get("name")}
        location_basis = "measured per-location contributors"
        location_complete = bool(supplied_location_scope.get("complete"))
        location_missing = supplied_location_scope.get("missingInput")
        if not location_missing and supplied_location_scope.get("missing"):
            location_missing = "; ".join(str(x) for x in supplied_location_scope.get("missing") or [])
        if not location_missing and supplied_location_scope.get("limits"):
            location_missing = " ".join(str(x) for x in supplied_location_scope.get("limits") or [])
        if not selected_plugins:
            selected_plugins = {n.lower() for n in evidence_plugins_by_arm.get(arm_id, set()) if n}
            location_basis = "plugins named by accepted target-conflict evidence"
            location_complete = False
            # the fallback list is only the target's recorded conflict partners, so it says so, and names a compared
            # patch this setup loads that the list leaves out rather than let the list read as the setup's order
            location_missing = ("This list names only plugins that appear in recorded conflicts involving the target mods. "
                                + (location_missing or
                                   "This bundle does not enumerate every plugin that changes the listed cells or worldspace coordinates.")
                                + "".join(f" This setup also loads {n}, which is not listed." for n in names
                                          if n in supplied and n.lower() not in selected_plugins))
        affecting_subset = [n for n in names if n and n.lower() in selected_plugins]
        arms_out.append({"armId": arm_id, "kind": kinds.get(arm_id), "displayLabel": display_label,
                         "candidateFile": da.get("candidateFile") or pa.get("candidateFile"),
                         "pluginsAdded": added, "pluginsRemoved": removed,
                         "payloadNote": (da.get("payload") or pa.get("payload") or {}).get("note"),
                         "espOnly": (da.get("payload") or pa.get("payload") or {}).get("espOnly"),
                         "loadedInGameNote": da.get("loadedInGameNote") or pa.get("loadedInGameNote"),
                         "readerLimits": setup_limits,
                         "locationPluginOrder": affecting_subset,
                         "locationPluginScope": {"basis": location_basis, "complete": location_complete,
                                                 "missingInput": location_missing,
                                                 "locations": [((f"{x.get('worldspace')} ({x.get('x')}, {x.get('y')})"
                                                                  if x.get('worldspace') and x.get('x') is not None and x.get('y') is not None
                                                                  else x.get('name') or x.get('cell')))
                                                               for x in supplied_locations if isinstance(x, dict)] or scope_locations},
                         "orderEvaluation": da.get("orderEvaluation"),
                         "identity": {"pluginCount": len(names), "unstatedRows": unstated, "identityLimits": limits}})
    # Several alternatives can share one display label (every removal-only arm is "Tested alternative setup").
    # A per-setup sentence then names WHICH one in the setups-compared list's own words, so a result on one
    # alternative is never read as another's and no-problem lines are never merged across them. A label no other
    # arm carries is kept as it is. Arms whose changes are also identical are numbered in the order the setups-compared
    # list shows them ("2 of 2 setups with this change"), and that list's row carries the same words, so the label
    # still points at one row without exposing the technical setup id.
    def change_words(arm) -> str:
        return "; ".join(([f"adds {', '.join(arm['pluginsAdded'])}"] if arm.get("pluginsAdded") else []) +
                         ([f"removes {', '.join(arm['pluginsRemoved'])}"] if arm.get("pluginsRemoved") else []))

    shared_labels = collections.Counter(a["displayLabel"] for a in arms_out)
    for a in arms_out:
        words = change_words(a) if shared_labels[a["displayLabel"]] > 1 else ""
        a["readerLabel"] = f"{a['displayLabel']} ({words})" if words else a["displayLabel"]
    shared_labels = collections.Counter(a["readerLabel"] for a in arms_out)
    seen_labels = collections.Counter()
    for a in arms_out:
        shared = a["readerLabel"]
        if shared_labels[shared] > 1:
            seen_labels[shared] += 1
            words = change_words(a)
            a["setupOrdinal"] = (f"{seen_labels[shared]} of {shared_labels[shared]} setups with "
                                 + ("this change" if words else "no plugin change"))
            a["readerLabel"] = f"{a['displayLabel']} ({words + '; ' if words else ''}{a['setupOrdinal']})"
    arm_labels = {a["armId"]: a["readerLabel"] for a in arms_out}
    # a list of locations several setups share is printed once, at the first setup that has it; later ones point at it
    first_with = {}
    for a in arms_out:
        locs = tuple(a["locationPluginScope"].get("locations") or [])
        a["locationPluginScope"]["sameAs"] = first_with.get(locs) if locs else None
        if locs:
            first_with.setdefault(locs, a["readerLabel"])
    # the page prints a list up to the skill's bound and counts the rest, which the evidence index lists in full
    # (Gate C round 1, H2: one footprint's list took about seven pages)
    lo_words = reader_data.get("locationOrder") or {}
    bound = lo_words.get("maxLocations")
    for a in arms_out:
        locs = list(a["locationPluginScope"].get("locations") or [])
        k = bound if isinstance(bound, int) and bound > 0 else len(locs)
        a["locationPluginScope"]["shown"] = locs[:k]
        a["locationPluginScope"]["moreLine"] = (bind(lo_words["moreLocations"], {"count": fmt_n(len(locs) - k)})
                                                if len(locs) > k else None)
    for f in findings:
        f["readerArmLabel"] = arm_labels.get(f.get("armId"), "Measured setup")
        c = f.get("counts") or {}
        if f.get("kind") != "finding" or c.get("groupKind") != "CLOBBERED" or not c.get("A") or not c.get("B"):
            continue
        arm = next((x for x in arms_out if x.get("armId") == f.get("armId")), None)
        pa = plan_arms.get(f.get("armId")) or {}
        order = [p.get("name") for p in ((pa.get("requested") or {}).get("plugins") or []) if p.get("name")]
        direction = infer_clobber_direction(f, order)
        if direction:
            f["overwriteDirection"] = direction
            f["readerPlain"] = (f"{direction['winningPlugin']}, loaded later, overwrites {fmt_n(f.get('rowsTotal'))} "
                                f"{noun(f.get('rowsTotal'), 'navmesh edit')} from {direction['overwrittenPlugin']}.")
    # Context-only overwrite history remains fully indexed, but is never presented as a compatibility finding.
    # A candidate is called pre-existing only when the same structured group and every record key occur at baseline.
    baseline_context = {}
    for f in findings:
        c = f.get("counts") or {}
        if f.get("armId") == "baseline" and context_only(f.get("checkId"), c.get("groupKind")):
            baseline_context[(f.get("checkId"), c.get("groupKind"), c.get("A"), c.get("B"))] = {
                r.get("formKey") for r in f.get("allRows") or [] if r.get("formKey")}
    for f in findings:
        c = f.get("counts") or {}
        if not context_only(f.get("checkId"), c.get("groupKind")):
            continue
        keys = {r.get("formKey") for r in f.get("allRows") or [] if r.get("formKey")}
        prior = baseline_context.get((f.get("checkId"), c.get("groupKind"), c.get("A"), c.get("B")))
        if f.get("armId") != "baseline" and keys and prior is not None and keys <= prior:
            f["readerContext"] = "Already present before adding the patch; this remains overwrite history, not an established incompatibility."
        else:
            f["readerContext"] = "Recorded overwrite history for this setup; it does not by itself establish an incompatibility or attribute the result to the patch."
    # Imported MCP evidence: the reader sees a practical qualification beside the result it affects (reused: not
    # measured for this evaluation, source stated not re-checked; origin unknown: not recorded whether it was);
    # fresh stays unremarked. Plan, arm, hash and receipt detail go to the evidence index. Counts are untouched.
    origin_words = reader_data.get("importOrigin") or {}
    imports = imported_evidence(b)
    by_rid = {rid: rc for rid, rc in b.receipts.items() if isinstance(rc.get("imported"), dict)}
    imports["limit"] = origin_words.get("limit")
    import_by_key = {(r["checkId"], r["armId"]): r for r in imports["rows"]}
    for row in imports["rows"]:
        row["receiptEvidenceId"] = eid_by_path.get(row["receiptPath"])
        # The importer's own note about what this result IS. Reuse provenance ("captured for
        # another evaluation") and correction provenance ("re-interpreted under corrected
        # rules") answer different questions, and a reader shown only the first cannot tell
        # that the numbers were recomputed. The note is the import's statement about itself,
        # carried verbatim and attributed, never summarised into a claim of our own.
        rc_note = ((by_rid.get(row.get("receiptId")) or {}).get("result") or {}).get("note")
        row["note"] = rc_note if isinstance(rc_note, str) and rc_note.strip() else None
        # Reuse between two setups of THIS evaluation is a different statement from reuse across evaluations, so it
        # has its own sentence; a receipt that does not say which keeps the cautious cross-evaluation wording.
        row["originWording"] = ("reusedWithinEvaluation"
                                if row["origin"] == "reused" and row.get("reusedWithin") == "this-evaluation"
                                else row["origin"])
        row["readerProvenance"] = origin_words.get(row["originWording"]) or None
        row["indexOrigin"] = origin_words.get("index", {}).get(row["originWording"])
        row["findingIds"] = []
    for f in findings:
        row = import_by_key.get((f.get("checkId"), f.get("armId")))
        if row is None or f.get("kind") not in ("finding", "null", "catalogue") or f.get("part") is not None:
            continue
        f["importOrigin"] = row["origin"]
        f["readerProvenance"] = row["readerProvenance"]
        row["findingIds"].append(f["id"])
    # Distinct import notes, in first-seen order. One sentence per DISTINCT note, not one per
    # import: twelve identical copies of the same explanation is noise, and the reader needs
    # the statement once.
    import_notes = list(dict.fromkeys(r["note"] for r in imports["rows"] if r.get("note")))
    imports["notes"] = import_notes
    missing = ev.get("missing") or []

    def reader_status(m) -> str:
        status = str(m.get("status"))
        state = comparison_state.get((m.get("checkId"), m.get("armId"))) if m.get("part") == "diff" else None
        if status == "completed" and state:
            if state["refused"]:
                return "comparison-refused|" + refused_words(state)
            if state["unmeasured"]:
                return f"comparison-unmeasured|{state['unmeasured']}"
        if status == "completed" and not state:
            return "completed(undetermined)"   # it ran and its detector reached no verdict
        return status
    reader_missing = [{**m, "status": reader_status(m)} for m in missing]
    by_cause = collections.Counter(str(m.get("status")) for m in missing)
    run_ids = b.manifest.get("runIds") or []
    scope_out = {"targets": targets, "registryVersion": b.plan.get("registry", {}).get("registryVersion") or disp.get("registryVersion"),
                 "planId": b.plan.get("planId"), "planCreatedUtc": b.plan.get("createdUtc"),
                 "runStartedUtc": (run_ids[0].get("startedUtc") if run_ids else None),
                 "bundleCreatedUtc": b.manifest.get("createdUtc"),
                 "scopeGeneratedUtc": (b.scope or {}).get("generatedUtc") if isinstance(b.scope, dict) else None,
                 "dispositionCreatedUtc": disp.get("createdUtc"),
                 "locationScope": {"locations": scope_locations,
                                   "basis": "locations recorded in the target footprint"},
                 "arms": arms_out, "states": b.plan.get("states") or {"selected": [], "notExplored": []},
                 "completion": {"complete": bool(ev.get("complete")), "claim": ev.get("claim"), "missingRequired": len(missing),
                                "byCause": dict(sorted(by_cause.items())), "unknownApplicability": ev.get("unknownApplicability") or []},
                 "integrity": ev.get("integrity") or {"ok": None, "defects": []},
                 "explicitDecisions": {"select": (disp.get("explicitDecisions") or {}).get("select"),
                                       "exclude": (disp.get("explicitDecisions") or {}).get("exclude")},
                 "checks": {cid: {"title": c.get("title"), "family": c.get("family"), "required": c.get("required"),
                                  "strength": (c.get("evidence") or {}).get("strength"), "claim": (c.get("evidence") or {}).get("claim"),
                                  "limits": (c.get("evidence") or {}).get("limits")} for cid, c in sorted(b.checks.items())}}

    # --- chapters (the reel's plan; D6 renders them)
    reel = pr.get("reel") or {}
    chapters = []
    claim = ev.get("claim")
    claim_sentence = bind((pr.get("claimSentences") or {}).get(claim or "incomplete", "{claim}"),
                          {"missingRequired": len(missing), "claim": claim})
    target_names = [t["name"] for t in targets if t.get("name")]
    target_set = {str(x).lower() for x in target_names}
    comparison_weight = collections.Counter()
    for f in findings:
        if f.get("readerRelevant") is False:
            continue
        c = f.get("counts") or {}
        pair = [str(x) for x in (c.get("A"), c.get("B")) if x]
        if not any(x.lower() in target_set for x in pair):
            continue
        for name in pair:
            if name.lower() not in target_set and name not in supplied:
                comparison_weight[name] += max(1, int(f.get("rowsTotal") or 0))
    # a difference with the base game is set aside, never a conflict partner named in the opening
    base_game_partners = [n for n in comparison_weight if is_base_game(n, reader_data.get("prediction") or {})]
    comparison_names = [n for n, _ in sorted(comparison_weight.items(), key=lambda x: (-x[1], x[0].lower()))
                        if n not in base_game_partners]
    reader_scope_names = target_set | {x.lower() for x in supplied}
    for f in findings:
        c = f.get("counts") or {}
        pair = {str(x).lower() for x in (c.get("A"), c.get("B")) if x}
        f["readerRelevant"] = bool(f.get("readerRelevant", True)) and (not pair or bool(pair & reader_scope_names))
    # The detail list shows only clashes between two different mods. A result within one mod, against the base game,
    # or naming no pair at all (a count only) is set aside to the evidence index with its reason; readerRelevant is
    # unchanged, so the verdict, the opening and the reel read what they read before.
    aside_counts = collections.Counter()
    for f in findings:
        if f.get("kind") != "finding":
            continue
        c = f.get("counts") or {}
        a_, b_ = c.get("A"), c.get("B")
        aside = ("countOnly" if not (a_ and b_) else "sameMod" if str(a_).lower() == str(b_).lower() else
                 "baseGame" if (is_base_game(a_, reader_data.get("prediction") or {})
                                or is_base_game(b_, reader_data.get("prediction") or {})) else None)
        f["readerDetail"] = aside is None
        f["readerDetailAside"] = aside
        if aside and f.get("readerRelevant"):
            aside_counts[aside] += 1
    # A record group between one evaluated mod and a mod that is neither evaluated nor a declared patch is not listed
    # in the detail list (Gate C round 1, H2: listing each such pair once per setup, then once per pair, still broke
    # the page limit). It goes to the evidence index (`singleMod`), counted in one sentence; the prediction lists
    # those clashes under their own heading. Clashes between the evaluated mods, or with a patch, stay listed.
    supplied_lower = {str(x).lower() for x in supplied}
    single_mod_count = 0
    for f in findings:
        if not (f.get("kind") == "finding" and f.get("readerDetail") and f.get("family") == "records"):
            continue
        c = f.get("counts") or {}
        pair = {str(x).lower() for x in (c.get("A"), c.get("B"))}
        if len(pair & target_set) != 1 or pair & supplied_lower:
            continue
        f["readerDetail"] = False
        f["readerDetailAside"] = "singleMod"
        if f.get("readerRelevant"):
            single_mod_count += 1
    compared =", ".join(comparison_names) if comparison_names else "the measured load order"
    unstated_text = (f" One tested setup differs by {', '.join(unstated_difference_plugins)}, "
                     f"which is not declared as a patch; it is reported as a plugin that "
                     f"differs, not as an evaluated patch."
                     if unstated_difference_plugins else "")
    patch_text = ", ".join(supplied) if supplied else "no separate patch plugin"
    # One sentence for declared patches a mixed arm changes; number agreement is its only branch.
    _mixed_setup = ("The tested setup changes" if sum(1 for k in comparison_kinds.values() if k == "mixed") == 1
                    else "The tested setups change")
    mixed_declared_text = ((f"{_mixed_setup} the declared patches {', '.join(mixed_declared_patches)} at once, "
                            f"so none of them is evaluated on its own." if len(mixed_declared_patches) > 1 else
                            f"{_mixed_setup} the declared patch {mixed_declared_patches[0]} together with another "
                            f"plugin, so it is not evaluated on its own.")
                           if mixed_declared_patches else "")
    patch_adds = any(n in supplied for added, _ in arm_changes.values() for n in added)
    patch_removes = any(n in supplied for _, removed in arm_changes.values() for n in removed)
    patch_how = ("added or removed for comparison" if patch_adds and patch_removes else
                 "included in the measured load order and removed for comparison" if patch_removes else
                 "added to the measured load order for comparison")
    scope_head = f"This report evaluates {', '.join(target_names) or 'the target mod'} within the measured load order. "
    scope_line = (scope_head
                  + (f"The main recorded conflicts involving the target are with {compared}. " if comparison_names else
                     ("The recorded conflicts involving the target are with the base game only; they are set aside "
                      "below, as differences rather than clashes between mods. " if base_game_partners else
                      "No comparison mod was identified by the recorded conflicts. "))
                  + " ".join(x for x in (
                      ((f"The patch evaluated is {patch_text}, {patch_how}." if len(supplied) == 1 else
                        f"The patches evaluated are {patch_text}, {patch_how}.") if supplied else
                       # never claim no patch was declared while a changed plugin is a declared patch
                       "" if mixed_declared_patches else
                       "No separate patch plugin was declared for this comparison."),
                      mixed_declared_text) if x)
                  + unstated_text)
    scope_tail = (" ".join(x for x in (
                      ((f"The patch evaluated is {patch_text}, {patch_how}." if len(supplied) == 1 else
                        f"The patches evaluated are {patch_text}, {patch_how}.") if supplied else
                       "" if mixed_declared_patches else
                       "No separate patch plugin was declared for this comparison."),
                      mixed_declared_text) if x)
                  + unstated_text)
    for _a in arms_out:
        _a["comparisonKind"] = comparison_kinds.get(_a.get("armId"))
        if arm_replaced.get(_a.get("armId")):
            _a["replacedPlugins"] = list(arm_replaced[_a.get("armId")])
        if comparison_kinds.get(_a.get("armId")) == "bytes-not-compared":
            _a["bytesNotCompared"] = list(arm_unverified[_a.get("armId")])
    alternative_arms = [a["armId"] for a in arms_out if a.get("kind") != "baseline"]
    candidate_orders = [a.get("orderEvaluation") or {} for a in arms_out if a.get("kind") != "baseline"]
    reader_regressions = [f for f in findings if f.get("kind") == "regression" and f.get("readerRelevant")]
    has_regression = bool(reader_regressions)
    overlap_created_total = sum(int((f.get("counts") or {}).get("readerCreated") or 0)
                                for f in reader_regressions if f.get("checkId") == "geometry.object-overlap")
    has_other_regression = any(f.get("checkId") != "geometry.object-overlap" for f in reader_regressions)
    repaired_total = sum(int((f.get("counts") or {}).get("readerRepaired") or 0) for f in findings
                         if f.get("kind") in ("repaired", "reclassified") and f.get("readerRelevant")
                         and f.get("checkId") != "geometry.object-overlap")
    repair_arms = {f.get("armId") for f in findings if f.get("kind") in ("repaired", "reclassified") and f.get("readerRelevant")
                   and f.get("checkId") != "geometry.object-overlap" and int((f.get("counts") or {}).get("readerRepaired") or 0)}
    overlap_reduction_total = sum(int((f.get("counts") or {}).get("readerRepaired") or 0) for f in findings
                                  if f.get("kind") in ("repaired", "reclassified") and f.get("readerRelevant")
                                  and f.get("checkId") == "geometry.object-overlap")
    overlap_reduction_arms = {f.get("armId") for f in findings if f.get("kind") in ("repaired", "reclassified")
                              and f.get("readerRelevant") and f.get("checkId") == "geometry.object-overlap"
                              and int((f.get("counts") or {}).get("readerRepaired") or 0)}
    has_alternative = any(a.get("kind") != "baseline" for a in arms_out)
    has_completed_result = any(f.get("kind") in ("regression", "finding", "repaired", "reclassified", "null", "catalogue")
                               and f.get("readerRelevant") for f in findings)
    has_context_only = any(f.get("readerEvidenceReason") in ("context-only-kind", "context-only-or-inseparable")
                           for f in findings)
    def defect_phrase() -> str:
        # how many new problems, in which checks, and whether a comparison was not accepted as a result
        per_check = collections.OrderedDict()
        for f in reader_regressions:
            if f.get("checkId") == "geometry.object-overlap":
                continue
            per_check.setdefault(f.get("checkId"), (f, [], []))[1].append(int((f.get("counts") or {}).get("readerCreated") or 0))
            if (comparison_state.get((f.get("checkId"), f.get("armId"))) or {}).get("notAccepted"):
                per_check[f.get("checkId")][2].append(True)
        parts = []
        for check_id, (f, ns, unacc) in per_check.items():
            title_ = (reader_data.get("checkTitles") or {}).get(check_id, f.get("title"))
            if len(ns) == 1:
                parts.append(f"{fmt_n(ns[0])} new {'problem' if ns[0] == 1 else 'problems'} in the check \"{title_}\"")
            else:                              # one count per setup, never summed across setups
                parts.append(f"new problems in the check \"{title_}\" in {len(ns)} setups ("
                             + ", ".join(fmt_n(n_) for n_ in ns) + ")")
            if unacc:
                parts[-1] += cmp_words["verdictNotAccepted"]
        return " and ".join(parts) if parts else "a new defect in a check that ran"

    if has_regression:
        if has_other_regression:
            change_subject = setup_subject({f.get("armId") for f in reader_regressions
                                            if f.get("checkId") != "geometry.object-overlap"} | repair_arms)
            if repaired_total and repair_claimable(repair_arms):
                verdict = (f"{change_subject} resolves some measured conflicts, but it also "
                           f"introduces {defect_phrase()}.")
            elif repaired_total:
                verdict = (f"{change_subject} records fewer conflicts than the measured load "
                           f"order and also introduces {defect_phrase()}. "
                           + non_repair_reason(repair_arms))
            else:
                verdict = f"{change_subject} introduces {defect_phrase()}."
            if overlap_created_total:
                verdict += bind(reader_verdict_templates["overlapAlongsideDefect"],
                                {"createdCount": overlap_created_total,
                                 "candidateNoun": overlap_candidate_noun(overlap_created_total)})
        else:
            verdict = bind(reader_verdict_templates["overlapOnlyRegression"],
                           {"createdCount": overlap_created_total,
                            **subject_values({f.get("armId") for f in reader_regressions}),
                            "candidateNoun": overlap_candidate_noun(overlap_created_total)})
        if overlap_reduction_total:
            verdict += reader_verdict_templates["overlapReductionAlongsideRegression"]
    elif repaired_total and repair_claimable(repair_arms):
        verdict = f"{setup_subject(repair_arms)} resolves some measured conflicts and no new defect was reported by the checks that ran."
    elif repaired_total:
        # Fewer conflicts, but produced by an arm that cannot carry a repair reading. Say what
        # was observed and refuse the repair reading -- then say what happened to the patch
        # comparison SEPARATELY, because which arm improved and whether a patch arm exists are
        # different questions. Appending "no setup containing a patch was compared"
        # unconditionally denied a declared patch comparison that had in fact been measured,
        # whenever the reduction happened to come from a removal control.
        patch_arms = [a for a in alternative_arms
                      if comparison_kinds.get(a) == "patch-comparison"]
        if not patch_arms:
            patch_clause = (" No setup containing a patch was compared, so these results do "
                            "not say whether a patch would resolve them.")
        else:
            patch_named = ", ".join(supplied) if supplied else "a patch"
            patch_repaired = any(a in repair_arms for a in patch_arms)
            patch_regressed = any(a in {f.get("armId") for f in reader_regressions}
                                  for a in patch_arms)
            if patch_repaired:
                patch_clause = ""          # handled by the repair branch above
            elif patch_regressed:
                patch_clause = (f" A setup containing {patch_named} was also compared, and it "
                                f"introduced a new defect in a check that ran.")
            else:
                patch_clause = (f" A setup containing {patch_named} was also compared; the "
                                f"checks that ran recorded no reduction and no new defect from "
                                f"it, so the patch was tested and this result is not a "
                                f"statement that it went untested.")
        verdict = (f"{setup_subject(repair_arms)} records fewer conflicts than the measured "
                   f"load order, and no new defect was reported by the checks that ran. "
                   + non_repair_reason(repair_arms) + patch_clause)
    elif overlap_reduction_total:
        verdict = bind(reader_verdict_templates["overlapOnlyReduction"], subject_values(overlap_reduction_arms))
    elif has_context_only and not any(f.get("kind") in ("regression", "finding") and f.get("readerRelevant") for f in findings):
        verdict = ("The available results record navmesh overwrite history, but they cannot determine incompatibility "
                   "without a measured mismatch in the winning scene or links.")
    elif claim == "no-findings-in-completed-checks":
        verdict = "The completed checks found no problem in what they examined."
    elif has_alternative:
        verdict = f"The completed checks do not establish that {lower_first(setup_subject(alternative_arms))} improves the target mod."
    elif any(f.get("kind") == "finding" and f.get("readerRelevant") for f in findings):
        verdict = "The completed checks found conflicts involving the target mod; no alternative setup was supplied for comparison."
    elif has_completed_result:
        verdict = "The completed checks found no problem in what they examined; no alternative setup was supplied for comparison."
    else:
        verdict = "The available results cannot determine whether the target mod has a problem in the measured setup."
    limitations = []
    if missing:
        limitations.append("Testing is incomplete; outstanding areas are listed below.")
    if not any(m.get("assessed") and m.get("mediaKind") == "game-video" for m in media):
        limitations.append("No matching play-time evidence was supplied, so this does not show what happened during play.")
    if any(a.get("espOnly") for a in arms_out):
        limitations.append("The supplied patch was tested as a plugin only; its meshes, scripts and installer choices were not included.")
    if has_context_only:
        limitations.append("Navmesh overwrite history is retained in the evidence index as context and is not treated as proof of incompatibility.")
    scope_limits = reader_data.get("scopeLimits") or {}
    other_limits = [scope_limits.get(x.get("extent"), "An additional area could not be assessed; see the full scope in the evidence index.")
                    for x in un.get("scopeUnmeasured") or [] if isinstance(x, dict)]
    # the areas not measured and the objects not judged are stated with their counts under the coverage heading;
    # this list keeps only what that heading does not say (P53: each was printed twice)
    other_limits = []
    if unassessed_media:
        other_limits.append("Some supplied images or clips could not be matched to this evaluation and were excluded from the evidence shown.")
    # One bullet, not one per import. Notes that embed per-import counts differ only in their
    # tails, so deduplicating on the whole string leaves a dozen near-identical paragraphs --
    # the reader needs the explanation once. Emit the shared opening once and send the
    # per-import remainder to the evidence index, which is the reference document and keeps
    # every note in full. Nothing is summarised: the shared text is the importer's own, cut
    # only at a sentence boundary.
    if import_notes:
        # The shared text is every SENTENCE present in all the notes, in the order the first
        # note gives them -- not the common prefix. A note that puts per-import figures in the
        # middle has common sentences on BOTH sides of them, and a prefix rule silently drops
        # everything after the first difference. That is how "this correction was made after
        # the subject was inspected" fell off the page while surviving in the index.
        def sentences(text):
            return [x.strip() for x in re.split(r"(?<=[.!?])\s+", text.strip()) if x.strip()]
        note_sentences = [sentences(n) for n in import_notes]
        common = set(note_sentences[0]).intersection(*(set(x) for x in note_sentences[1:]))             if len(note_sentences) > 1 else set(note_sentences[0])
        shared = " ".join(x for x in note_sentences[0] if x in common)

        # Many notes can share no whole sentence when each names its own setup and file in the sentence that says
        # what the evidence is ("REUSED evidence, not freshly measured: captured ... arm X; ..."); the clause they
        # all share still reaches the page, once (Gate C round 1: Group 6's sixteen notes took two pages).
        def clauses(text):
            return [x.strip() for x in re.split(r"(?<=[.!?;:])\s+", text.strip()) if x.strip()]
        note_clauses = [clauses(n) for n in import_notes]
        common_c = set(note_clauses[0]).intersection(*(set(x) for x in note_clauses[1:]))
        # a shared clause of fewer than four words ("Rows:", a file name) says nothing on its own
        shared_c = " ".join(re.sub(r"[;:]$", ".", x) for x in note_clauses[0] if x in common_c and len(x.split()) >= 4)
        if len(import_notes) > 1 and len(shared) >= 40:
            other_limits.append(
                "The imported results in this report carry a note from the tool that produced "
                "them: " + shared + " Each import's own note, including the figures that "
                "differ between them, is in the evidence index.")
        elif len(import_notes) > 3:
            # a note's opening clause says what the evidence is; one at least half of them open with (say, half
            # reused and half freshly measured) is stated with its count
            seen_c = collections.Counter(cs[0] for cs in note_clauses if cs)
            half = [x for x in dict.fromkeys(cs[0] for cs in note_clauses if cs)
                    if x not in common_c and len(x.split()) >= 4 and seen_c[x] * 2 >= len(import_notes)]
            other_limits.append(
                f"The {fmt_n(len(import_notes))} imported results in this report carry notes from the tool that "
                "produced them, which differ from one import to another" +
                (f"; every one of them says: {shared_c}" if shared_c else ".") +
                "".join(f" {fmt_n(seen_c[x])} of them say: {re.sub(r'[;:]$', '.', x)}" for x in half) +
                " Each note in full is in the evidence index.")
        else:
            for note in import_notes:
                other_limits.append("The imported results in this report carry a note from the "
                                    "tool that produced them: " + note.strip())
    check_titles = reader_data.get("checkTitles") or {}
    reason_labels = reader_data.get("missingReasons") or {}

    def missing_reason(status: str, fallback: str) -> str:
        # `blocked(prerequisite:<check>)` carries the check it waited for; the reader is told which one, in its title
        blocked_by = PREREQUISITE_STATUS.match(status)
        if blocked_by and reader_data.get("missingPrerequisiteReason"):
            prerequisite = check_titles.get(blocked_by.group(1)) or b.check_title(blocked_by.group(1))
            return bind(reader_data["missingPrerequisiteReason"], {"prerequisite": lower_first(prerequisite)})
        if status.startswith("comparison-refused|"):
            return bind(cmp_words["missingRefused"], {"why": status.split("|", 1)[1]})
        if status.startswith("comparison-unmeasured|"):
            n_ = int(status.split("|", 1)[1])
            return bind(cmp_words["missingUnmeasured"], {"count": fmt_n(n_), "rowNoun": "row" if n_ == 1 else "rows"})
        return reason_labels.get(status, fallback)
    reason_explanations = reader_data.get("missingReasonExplanations") or {}
    # Grouped by status, as the reason is a statement about the status - but each subject names the setup it is
    # missing ON, in the setups-compared list's own words. The disposition's missing list is per arm, so a check
    # supplied on one setup and missing on another is never told as a statement about the evaluation as a whole.
    # The no-problem list below already names the setup this way.
    arm_order = {a["armId"]: i for i, a in enumerate(arms_out)}
    def summarise_missing(rows_in):
        missing_summary = []
        statuses_by_check = collections.OrderedDict()
        for m in rows_in:
            sts = statuses_by_check.setdefault(str(m.get("checkId")), [])
            if str(m.get("status")) not in sts:
                sts.append(str(m.get("status")))
        for status_set in sorted({tuple(sorted(v)) for v in statuses_by_check.values()}):
            rows_ = []
            for m in rows_in:
                if tuple(sorted(statuses_by_check[str(m.get("checkId"))])) != status_set:
                    continue
                title = check_titles.get(str(m.get("checkId")), b.check_title(m.get("checkId")))
                rows_.append((title, arm_order.get(m.get("armId"), len(arm_order)),
                              arm_labels.get(m.get("armId"), "Measured setup")))
            subjects = [f"{title} — {setup}" for title, _, setup in sorted(dict.fromkeys(rows_))]
            missing_summary.append({"reason": "; and ".join(missing_reason(st, "has no accepted result") for st in status_set),
                                    "subjects": subjects,
                                    "explanation": " ".join(x for x in (reason_explanations.get(st) for st in status_set) if x)
                                    or None})
        return missing_summary
    missing_summary = summarise_missing(reader_missing)
    null_summary = []
    comparison_summary = []
    kind_phrases_all = reader_data.get("kindPhrases") or {}

    def bare_phrase(phrase: str) -> str:
        return re.sub(r"^(a|an) ", "", phrase)

    def setup_totals_sentence(comparison) -> str:
        # The totals beside a no-additional-problem comparison. Each number counts the listed rows (records, or a
        # kind's own unit) of this check's findings on one setup: the measured load order's arm part, and this
        # setup's own measurement. They cover every plugin pair the check reported, including pairs the report
        # does not list because they involve neither a target nor a compared patch. Rows of a context-only kind
        # (navmesh overwrite history) are left out of both numbers but still counted by the probe - the evidence
        # index's "out of N conflicts" includes them - so the sentence names what it leaves out.
        def rows_on(arm_id, skip_parts):
            return [x for x in findings if x.get("kind") == "finding" and x.get("checkId") == comparison.get("checkId")
                    and x.get("armId") == arm_id and x.get("part") not in skip_parts]
        before = rows_on("baseline", ("reverse",))
        after = rows_on(comparison.get("armId"), ("diff", "reverse"))
        counted_before = [x for x in before if not x.get("readerEvidenceReason")]
        counted_after = [x for x in after if not x.get("readerEvidenceReason")]
        total_before = sum(int(x.get("rowsTotal") or 0) for x in counted_before)
        total_after = sum(int(x.get("rowsTotal") or 0) for x in counted_after)
        if not (total_before or total_after):
            return ""
        units = {(kind_phrases_all.get(str((x.get("counts") or {}).get("groupKind") or "")) or {}).get("unitPlural")
                 for x in counted_before + counted_after if int(x.get("rowsTotal") or 0)}
        unit_kind = (next((k for k, v in kind_phrases_all.items() if v.get("unitPlural") in units and v.get("unitSingular")), None)
                     if len(units) == 1 and None not in units else None)
        if unit_kind:
            one, many = kind_phrases_all[unit_kind]["unitSingular"], kind_phrases_all[unit_kind]["unitPlural"]
        else:
            one, many = "record with a problem", "records with a problem"
        sentence = (f" In total this check reported {fmt_n(total_before)} {one if total_before == 1 else many} "
                    f"in the measured load order and {fmt_n(total_after)} in this setup")
        if any(x.get("readerRelevant") is False and int(x.get("rowsTotal") or 0) for x in counted_before + counted_after):
            involved = "the target mods" + (" or a compared patch" if supplied else "")
            sentence += f", including conflicts that do not involve {involved}, which this report does not list"
        sentence += "."
        history_before = [x for x in before if x.get("readerEvidenceReason") == "context-only-kind"]
        history_after = [x for x in after if x.get("readerEvidenceReason") == "context-only-kind"]
        hist_before = sum(int(x.get("rowsTotal") or 0) for x in history_before)
        hist_after = sum(int(x.get("rowsTotal") or 0) for x in history_after)
        if hist_before or hist_after:
            kinds_ = {str((x.get("counts") or {}).get("groupKind") or "") for x in history_before + history_after
                      if int(x.get("rowsTotal") or 0)}
            phrase = kind_phrases_all.get(next(iter(kinds_))) if len(kinds_) == 1 else None
            if phrase and phrase.get("singular") and phrase.get("plural"):
                noun_ = bare_phrase(phrase["singular"]) if hist_before == 1 else phrase["plural"]
            else:
                noun_ = "record of overwrite history" if hist_before == 1 else "records of overwrite history"
            sentence += (f" Not counted: {fmt_n(hist_before)} {noun_} in the measured load order and "
                         f"{fmt_n(hist_after)} in this setup, kept in the evidence index as history rather than "
                         "as problems.")
        return sentence

    def holding_sentence(f) -> str:
        # rows that left the class with their defect still there are not "no problem" (the comparison's own count)
        n_ = int((comparison_state.get((f.get("checkId"), f.get("armId"))) or {}).get("stillHolding") or 0)
        return (" " + bind(cmp_words["stillHolding"], {"count": fmt_n(n_), "verb": "is" if n_ == 1 else "are"})
                if n_ else "")

    for f in findings:
        if f.get("kind") != "null":
            continue
        subject = check_titles.get(str(f.get("checkId")), f.get("title"))
        if f.get("part") in ("diff", "reverse"):
            if f.get("part") == "diff":
                comparison_summary.append(f"{subject} — {f.get('readerArmLabel')}: {f.get('readerPlain')}"
                                          f"{setup_totals_sentence(f)}{holding_sentence(f)}")
            continue
        context = (f.get("readerPlain") or ("the comparison found no additional issue" if f.get("part") == "diff" else
                                            "no problem was found in the records examined")).rstrip(".")
        if holding_sentence(f):
            context += "." + holding_sentence(f).rstrip(".")
        if f.get("readerProvenance"):
            context += ". " + str(f["readerProvenance"]).rstrip(".")
        null_summary.append(f"{subject} — {f.get('readerArmLabel')}: {context}")
    null_summary = sorted(set(null_summary))
    for (check_id, arm_id), state in sorted(comparison_state.items()):
        if state["refused"] and arm_id != "baseline":
            comparison_summary.append(f"{check_titles.get(str(check_id), b.check_title(check_id))} — "
                                      f"{arm_labels.get(arm_id, 'Measured setup')}: "
                                      + bind(cmp_words["refusedLine"], {"why": refused_words(state)}))
    catalogue_summary = sorted({check_titles.get(str(f.get("checkId")), f.get("title"))
                                for f in findings if f.get("kind") == "catalogue"})
    catalogue_summary += sorted({f"{check_titles.get(str(f.get('checkId')), f.get('title'))} — {f.get('readerArmLabel')}: "
                                 f"{str(f['readerProvenance']).rstrip('.')}"
                                 for f in findings if f.get("kind") == "catalogue" and f.get("readerProvenance")})
    title_subject = ", ".join(target_names) or "Target mod"
    alternative_subject = lower_first(setup_subject(alternative_arms))
    # A patch review names the patch. With no separate patch the setups differ only by the target mods themselves,
    # so the title says what was evaluated against what: the target within the measured load order. The mods its
    # recorded conflicts involve are named in the opening as the main conflicts found, not in the title as the
    # comparison.
    title = (f"{title_subject} with {', '.join(supplied)} — patch review" if supplied else
             f"{title_subject} within the measured load order — conflict review")
    reader = {"title": title,
              "scopeLine": scope_line, "verdict": verdict, "limitations": limitations,
              "sectionHeadings": {"regressions": f"Problems introduced by {alternative_subject}",
                                  "changes": f"Changes made by {alternative_subject}"},
              "comparisonMods": comparison_names, "suppliedPatches": supplied, "checkTitles": check_titles,
              "locationOrder": reader_data.get("locationOrder") or {},
              "otherLimits": list(dict.fromkeys(other_limits)),
              "checkExplanations": reader_data.get("checkExplanations") or {},
              "missingSummary": missing_summary, "nullSummary": null_summary,
              "comparisonSummary": comparison_summary,
              "catalogueSummary": catalogue_summary,
              "context": {"evaluatedAt": disp.get("createdUtc") or b.manifest.get("createdUtc"),
                          "statesExamined": (b.plan.get("states") or {}).get("selected") or [],
                          # the plan's own list and the classes the census could not express, in words, once each
                          "statesNotExamined": list(dict.fromkeys(
                              [str(x) for x in (b.plan.get("states") or {}).get("notExplored") or []]
                              + [((reader_data.get("prediction") or {}).get("stateClassWords") or {}).get(
                                  str(x.get("code") or x.get("subject"))) or str(x.get("subject"))
                                 for x in un.get("statesNotExplored") or [] if isinstance(x, dict)]))}}
    # The report leads with the prediction (what adding the evaluated mods will break), then coverage, then the patch
    # as the answer key. The comparison sentence above keeps its words as the validation headline; the reader's
    # verdict -- and so the reel's opening -- is the prediction.
    lead = build_prediction({
        "findings": findings, "armOrders": arm_orders, "armIds": arm_ids, "targets": target_names,
        "declared": declared_patches, "comparisonKinds": comparison_kinds, "armChanges": arm_changes, "armReplaced": arm_replaced,
        "missing": reader_missing, "unassessed": un, "checkTitles": check_titles, "missingReason": missing_reason,
        "checkTitle": b.check_title, "armLabels": arm_labels,
        "familyTitle": lambda x: (b.families.get(x) or {}).get("title") or str(x),
        "cannotRunChecks": [str(c.get("checkId")) for c in b.plan.get("checks") or []
                            if isinstance(c, dict) and c.get("status") in ("unsupported", "manual")],
        "oldVerdict": verdict, "limitations": limitations, "words": reader_data["prediction"],
        "inGame": reader_data.get("inGame") or {}, "kindPhrases": reader_data.get("kindPhrases") or {},
        "mainBundle": b, "sibling": sibling, "comparisonState": comparison_state,
        "singleMod": settings.get("singleMod"), "headlineUnit": settings.get("headlineUnit")})
    verdict = lead["prediction"]["verdict"]
    # The scope line names the evaluated pair when the counted clashes need both of them, never the third mod with
    # the most rows; the third mods of single-mod clashes shown are named after it.
    pred_ = lead["prediction"]
    sw = reader_data.get("scopeWords") or {}
    if len(pred_["mods"]) > 1 and any(i["attribution"] == "needs-both" for i in pred_["items"]):
        pair = list(pred_["mods"])
        alone_mods = []
        for i in pred_["items"]:
            if i.get("shownAs") == "alone":
                alone_mods += [m for m in i["mods"] if m.lower() not in {x.lower() for x in pair} and m not in alone_mods]
        partner = bind(sw["pair"], {"modA": pair[0], "modB": " and ".join(pair[1:])}) + (
            " " + bind(sw["alone"], {"mods": ", ".join(alone_mods)}) if alone_mods and sw.get("alone") else "")
        scope_line = scope_head + partner + " " + scope_tail
        reader["scopeLine"] = scope_line
        reader["scopePair"] = pair
        reader["comparisonMods"] = alone_mods
    reader["verdict"] = verdict
    reader["prediction"] = lead["prediction"]
    # Evidence derived after measurement (bundle standard §8, enrichment): listed in the evidence index with what
    # it was derived from; names reach the plan so a reader's page needs no side input.
    reader["enrichment"] = [{k: e.get(k) for k in ("path", "sha256", "enrichment", "checkId", "armId", "derivedFrom")}
                            for e in b.enrichment()]
    reader["enrichmentLine"] = reader_data.get("enrichmentLine") if reader["enrichment"] else None
    names_row = next((e for e in b.enrichment() if e.get("enrichment") == "names"), None)
    names_doc = load_json(bundle_dir / names_row["path"]) if names_row else None
    reader["names"] = ({k: names_doc.get(k) for k in ("places", "objects", "mods", "missing", "derivedFrom")}
                       if isinstance(names_doc, dict) else None)
    reader["coverage"] = lead["coverage"]
    # P53 (accepted under the standing rule, 2026-10-17): in concise mode a check with no result on a setup the
    # coverage section already lists, by reason and with its setups, is not listed again on the page under "what
    # still needs checking"; one sentence points there. `missingSummary` stays complete in the plan;
    # `missingShown` is what the page lists.
    if settings.get("mode") != "exhaustive":
        covered = set(lead["coverage"].get("relevantArms") or [])
        rest = [m for m in reader_missing if m.get("armId") not in covered]
        reader["missingShown"] = summarise_missing(rest)
        if len(rest) < len(reader_missing):
            reader["missingCoveredLine"] = (reader_data.get("prediction") or {}).get("missingCoveredLine")
    reader["validation"] = lead["validation"]
    reader["sectionHeadings"].update(lead["headings"])
    pw = reader_data.get("prediction") or {}
    aside_total = sum(aside_counts.values())
    reader["detailAsideLine"] = (bind(pw["detailAsideLine"], {
        "count": fmt_n(aside_total), "resultNoun": "result" if aside_total == 1 else "results",
        "parts": "; ".join(f"{fmt_n(aside_counts[k])} {pw['detailAside'][k]}"
                           for k in ("baseGame", "sameMod", "countOnly") if aside_counts.get(k))})
                                 if aside_total else None)
    reader["detailSingleModLine"] = (bind(pw["detailSingleModLine"], {
        "count": fmt_n(single_mod_count), "resultNoun": "result" if single_mod_count == 1 else "results",
        "verb": "is" if single_mod_count == 1 else "are"}) if single_mod_count else None)
    # the `missing` chapter names every required missing check id, one caption per status group
    missing_by_status: dict = collections.OrderedDict()
    for m in missing:
        if m.get("required", True) is False:
            continue                                     # an optional check's missing row keeps nothing incomplete
        missing_by_status.setdefault(str(m.get("status")), []).append(m)
    missing_lines = []
    for status in sorted(missing_by_status):
        rows_ = missing_by_status[status]
        ids = sorted({str(m.get("checkId")) for m in rows_})
        missing_lines.append(f"{status} ({fmt_n(len(rows_))}): " + ", ".join(ids))
    chapters.append({"id": "C-01", "kind": "opening", "title": "Evaluation of " + ", ".join(t["name"] for t in targets if t.get("name")),
                     "findingIds": [], "mediaIds": [],
                     "captions": [{"text": f"Arms: " + ", ".join(a['armId'] for a in arms_out) + ". " + claim_sentence
                                   + (" Missing: " + "; ".join(missing_lines) + "." if missing_lines else ""),
                                   "evidenceIds": [eid_by_path.get("disposition.json")] if eid_by_path.get("disposition.json") else [],
                                   "mediaId": None}],
                     "plannedSeconds": reel.get("openingSeconds", 6)})
    n = 1
    media_by_id = {m["id"]: m for m in media}
    blocks: dict = collections.OrderedDict()
    for f in findings:
        if f["kind"] in ("regression", "finding") and f.get("readerRelevant"):
            blocks.setdefault((f["checkId"], f["armId"], f.get("part")), []).append(f)
    order = {f["id"]: i for i, f in enumerate(findings)}
    ordered_blocks = sorted(blocks.items(), key=lambda kv: (0 if any(x["kind"] == "regression" for x in kv[1]) else 1,
                                                          kv[0][0] or "", arm_sort_key(kv[0][1] or "", kinds),
                                                          PART_ORDER.get(kv[0][2], 9), order[kv[1][0]["id"]]))

    def seconds_for(mid):
        m = media_by_id.get(mid) if mid else None
        secs = reel.get("imageSeconds", 5)
        if m and isinstance(m.get("sourceInterval"), dict):
            si = m["sourceInterval"]
            try:
                secs = max(1, float(si.get("endSeconds", 0)) - float(si.get("startSeconds", 0)))
            except (TypeError, ValueError):
                pass
        return secs

    # one `finding` chapter per (checkId, armId, part) block; one caption per qualified media item bound to the
    # block, else an unbound scene that needs qualified media or an explicit reviewed omission.
    for (check_id, arm_id, part), fs in ordered_blocks:
        n += 1
        mids = []
        for f in fs:
            for mid in f.get("mediaIds") or []:
                if mid not in mids and mid in media_by_id and media_by_id[mid].get("assessed"):
                    mids.append(mid)
        captions = []
        for mid in mids:
            m = media_by_id[mid]
            owner = next(f for f in fs if mid in (f.get("mediaIds") or []))
            desc = str((m.get("subject") or {}).get("description") or "").strip()
            spoken = (desc + " " if desc else "") + owner["plain"]
            captions.append({"text": spoken, "narration": " ".join(x for x in (owner["readerPlain"], owner.get("readerProvenance")) if x),
                             "storyboard": {"mode": "full-frame"},
                             "evidenceIds": owner.get("evidenceIds") or [], "mediaId": mid})
        if not captions:
            head = fs[0]
            text = head["plain"] if len(fs) == 1 else (
                f"{check_id} on arm {arm_id} ({part or 'arm'}): {len(fs)} conflict group(s), "
                f"{fmt_n(sum(x['rowsTotal'] for x in fs))} row(s). " + head["plain"])
            captions.append({"text": text, "narration": " ".join(x for x in (head["readerPlain"], head.get("readerProvenance")) if x),
                             "storyboard": {"mode": "full-frame"},
                             "evidenceIds": sorted({e for f in fs for e in (f.get("evidenceIds") or [])}), "mediaId": None})
        chapters.append({"id": f"C-{n:02d}", "kind": "finding", "title": f"{fs[0]['title']} - {arm_id} / {part or 'arm'}",
                         "findingIds": [f["id"] for f in fs], "mediaIds": mids, "captions": captions,
                         "plannedSeconds": sum(seconds_for(c["mediaId"]) for c in captions)})
    for a in arms_out:
        oe = a.get("orderEvaluation") or {}
        if oe.get("classification"):
            per = oe.get("perProbe") or {}
            text = (f"Arm {a['armId']}: classification {oe.get('classification')}; " +
                    "; ".join(f"{p}: {v.get('verdict')} repaired {fmt_n(v.get('repaired'))} reclassified {fmt_n(v.get('reclassified'))} "
                              f"created {fmt_n(v.get('created'))}" for p, v in sorted(per.items())) + ". " + str(a.get("payloadNote") or ""))
            fids = [f["id"] for f in findings if f["armId"] == a["armId"] and f["kind"] in ("regression", "repaired", "reclassified")
                    and f.get("readerRelevant")]
            if not fids:
                continue
            n += 1
            chapters.append({"id": f"C-{n:02d}", "kind": "comparison", "title": f"Order evaluation: {a['armId']}", "findingIds": fids,
                             "mediaIds": [], "captions": [{"text": text, "evidenceIds": sorted({e for f in findings if f["id"] in fids for e in f["evidenceIds"]}),
                                                          "mediaId": None}],
                             "plannedSeconds": reel.get("imageSeconds", 5)})
    if missing:
        n += 1
        fids = [f["id"] for f in findings if f["kind"] == "missing"]
        chapters.append({"id": f"C-{n:02d}", "kind": "missing", "title": "Required checks with no accepted result", "findingIds": fids,
                         "mediaIds": [], "captions": [{"text": line, "evidenceIds": [], "mediaId": None} for line in missing_lines],
                         "plannedSeconds": reel.get("imageSeconds", 5) * len(missing_lines)})
    n += 1
    chapters.append({"id": f"C-{n:02d}", "kind": "closing", "title": "What this evaluation does not say", "findingIds":
                     [f["id"] for f in findings if f["kind"] == "unassessed"], "mediaIds": [],
                     "captions": [{"text": claim_sentence
                                   + (" Required checks still missing: " + "; ".join(missing_lines) + "." if missing_lines else "")
                                   + " Not assessed: " + ", ".join(sorted({f.get("area") or "" for f in findings if f["kind"] == "unassessed"})) + ".",
                                   "evidenceIds": [], "mediaId": None}],
                     "plannedSeconds": reel.get("closingSeconds", 8)})
    for chapter in chapters:
        for i, caption in enumerate(chapter.get("captions") or []):
            if chapter["kind"] == "opening":
                caption["narration"] = " ".join([verdict, scope_line] + limitations)
            elif chapter["kind"] == "closing":
                caption["narration"] = " ".join(limitations + ["Review the findings and unfinished checks in the report before choosing this setup."])
            elif chapter["kind"] == "missing":
                status = sorted(missing_by_status)[i]
                subjects = sorted({check_titles.get(str(m.get("checkId")), "an additional check")
                                   for m in missing_by_status[status]})
                reason = missing_reason(status, "Still needs checking")
                caption["narration"] = reason[:1].upper() + reason[1:] + ": " + "; ".join(subjects) + "."
            elif chapter["kind"] == "comparison":
                caption["narration"] = " ".join(f["readerPlain"] for f in findings
                                                 if f["id"] in chapter["findingIds"] and f["kind"] in ("regression", "repaired", "reclassified")) or "The completed checks do not establish an improvement for this setup."
            caption.setdefault("narration", "Review this scene alongside the findings in the report.")
            storyboard = caption.setdefault("storyboard", {})
            storyboard.setdefault("mode", "full-frame")
            storyboard.setdefault("cleanFrame", False)
            storyboard.setdefault("needsAuthorReview", True)
            storyboard.setdefault("visualReview", {"addedText": "none", "subjectVisible": False})

    plan = {
        "schemaVersion": PRESENTATION_SCHEMA,
        "presentationId": None,
        "generatedUtc": None,
        "source": {"bundleDir": bundle_dir_as_given, "planId": b.manifest.get("planId"), "bundleSchema": b.manifest.get("bundleSchema"),
                   "manifestSha256": sha256_file(bundle_dir / "manifest.json"),
                   "dispositionSha256": sha256_file(bundle_dir / b.manifest.get("disposition", "disposition.json")),
                   "ledgerSha256": sha256_file(bundle_dir / b.manifest.get("ledger", "ledger.json")),
                   "verify": {"bundleComplete": bool((verify.get("bundle") or {}).get("complete")),
                              "evaluationComplete": bool((verify.get("evaluation") or {}).get("complete")),
                              "claim": (verify.get("evaluation") or {}).get("claim")},
                   "verifiedFiles": (verify.get("bundle") or {}).get("verified")},
        "compiler": {"name": "evalreport", "version": COMPILER_VERSION, "sourceSha256": compiler_hashes()},
        "settings": settings,
        "scope": scope_out,
        "reader": reader,
        "reelNarration": {
            "opening": {"mediaId": None, "narration": verdict + " " + scope_line,
                        "storyboard": {"mode": "full-frame", "cleanFrame": False, "needsAuthorReview": True,
                                       "visualReview": {"addedText": "none", "subjectVisible": False}}},
            "closing": {"mediaId": None, "narration": " ".join(limitations),
                        "storyboard": {"mode": "full-frame", "cleanFrame": False, "needsAuthorReview": True,
                                       "visualReview": {"addedText": "none", "subjectVisible": False}}}
        },
        "claimSentence": claim_sentence,
        "imports": imports,
        "findings": findings,
        "groups": [groups[k] for k in sorted(groups)],
        "evidence": evidence,
        "media": media + unassessed_media,
        "unassessed": {**un, "media": unassessed_media},
        "omissions": omissions,
        "chapters": chapters,
        "narration": {"source": "template", "validated": True, "refused": []},
    }
    return plan


def compiler_hashes() -> dict:
    out = {"evalreport.py": sha256_file(Path(__file__).resolve())}
    for t in sorted(TEMPLATES.glob("*")) if TEMPLATES.exists() else []:
        out[f"templates/{t.name}"] = sha256_file(t)
    out["report-priorities.json"] = sha256_file(PRIORITIES)
    return out


def stamp(plan: dict) -> dict:
    body = {k: v for k, v in plan.items() if k not in ("presentationId", "generatedUtc")}
    plan["presentationId"] = sha256_text(canonical(body))[:12]
    plan["generatedUtc"] = utc_now()
    return plan


# ------------------------------------------------------------------------------------------ render --

def env(autoescape: bool):
    if jinja2 is None:
        raise SystemExit("REFUSED: jinja2 is not installed; the templates cannot be rendered (pip install jinja2)")
    e = jinja2.Environment(loader=jinja2.FileSystemLoader(str(TEMPLATES)), autoescape=autoescape,
                           trim_blocks=True, lstrip_blocks=True, keep_trailing_newline=True, undefined=jinja2.StrictUndefined)
    e.filters["n"] = fmt_n
    e.filters["slug"] = slug
    e.filters["md"] = lambda s: str(s if s is not None else "").replace("|", "\\|").replace("\n", " ")
    # the HTML index's counterpart to `md`: autoescaping handles the escaping, so all that is left is the
    # None-to-empty rule, which keeps an absent value blank in both indexes rather than printing "None"
    e.filters["txt"] = lambda s: str(s if s is not None else "")
    e.filters["short"] = lambda s, n=96: (str(s) if s is not None else "")[:n] + ("..." if s and len(str(s)) > n else "")
    e.filters["kv"] = fmt_kv
    e.filters["sentences"] = sentence_cut
    e.filters["tailpath"] = tail_path
    return e


def sentence_cut(s, n=220) -> str:
    """Cut at a sentence boundary within n characters; empty when no boundary falls past 40% of n."""
    s = str(s or "").strip()
    if len(s) <= n:
        return s
    head = s[:n]
    cut = max(head.rfind(". "), head.rfind("; "))
    if cut < int(n * 0.4):
        return ""
    return head[:cut + 1]


def tail_path(p) -> str:
    """`<supplier folder>/<basename>` - a shareable rendition carries no machine path."""
    parts = [x for x in re.split(r"[\\/]+", str(p or "")) if x]
    return "/".join(parts[-2:]) if len(parts) > 2 else "/".join(parts)


def fmt_kv(d) -> str:
    """`{'NoGeometry': 65}` -> `NoGeometry 65`; an empty or all-zero mapping reads `none`."""
    if not isinstance(d, dict):
        return "none" if d in (None, {}, [], "") else str(d)
    items = [(k, v) for k, v in d.items()]
    if not items or all((isinstance(v, (int, float)) and v == 0) for _, v in items):
        return "none"
    return ", ".join(f"{k} {fmt_n(v)}" for k, v in items)


def effective_view(plan: dict, rows_per_group: int, figures_per_finding: int, pr: dict, layout: str = "normal") -> dict:
    """The plan as the templates see it at one ladder step: rows shown per finding, figures per finding,
    omissions recomputed for the effective settings (exhaustive mode: everything, no ladder)."""
    mode = plan["settings"]["mode"]
    reverse_rule = (pr.get("concise") or {}).get("reversePartRows", "index-only")
    media_by_id = {m["id"]: m for m in plan.get("media") or []}
    findings = []
    omissions = []
    for f in plan["findings"]:
        g = dict(f)
        if mode == "exhaustive":
            shown = f["rowsTotal"]
        elif f.get("part") == "reverse" and reverse_rule == "index-only" and f["rowsTotal"] > 0:
            shown = 0
            omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'])} reverse-part row(s) not listed in the report",
                              "why": (pr.get("concise") or {}).get("reversePartNote", "index-only"),
                              "where": f"evidence-index.md#{f['id']}"})
        else:
            shown = min(f["rowsTotal"], rows_per_group)
            if shown < f["rowsTotal"]:
                omissions.append({"what": f"{f['id']}: {fmt_n(f['rowsTotal'] - shown)} of {fmt_n(f['rowsTotal'])} row(s) not listed in the report",
                                  "why": f"concise mode lists {rows_per_group} row(s) per group", "where": f"evidence-index.md#{f['id']}"})
        g["rowsShown"] = shown
        g["shownRows"] = (f.get("allRows") or f.get("rows") or [])[:shown]
        figs = [media_by_id[m] for m in (f.get("mediaIds") or []) if m in media_by_id and media_by_id[m].get("assessed")]
        if mode != "exhaustive" and len(figs) > figures_per_finding:
            omissions.append({"what": f"{f['id']}: {len(figs) - figures_per_finding} of {len(figs)} figure(s) not shown",
                              "why": f"concise mode shows {figures_per_finding} figure(s) per finding", "where": f"evidence-index.md#{f['id']}"})
            figs = figs[:figures_per_finding]
        g["figures"] = figs
        findings.append(g)
    sections = []
    for s in sorted(pr.get("sections") or [], key=lambda s: s.get("order", 99)):
        kinds = set(s.get("kinds") or [])
        areas = set(s.get("areas") or [])
        fs = [f for f in findings if f["kind"] in kinds and (not areas or f.get("area") in areas)]
        sections.append({**s, "findings": fs})
    # section 2, compact: one row per (status, check, cause) with the arms joined
    missing_rows = []
    seen = collections.OrderedDict()
    for f in findings:
        if f["kind"] != "missing":
            continue
        key = (str(f["counts"].get("status")), str(f["counts"].get("cause")))
        if key not in seen:
            seen[key] = {"status": key[0], "cause": f["counts"].get("cause"), "checks": collections.OrderedDict(), "findingIds": []}
        seen[key]["checks"].setdefault(f.get("checkId"), []).append(f.get("armId"))
        seen[key]["findingIds"].append(f["id"])
    for key in sorted(seen):
        row = seen[key]
        row["checks"] = [{"checkId": c, "arms": a} for c, a in row["checks"].items()]
        row["count"] = len(row["findingIds"])
        missing_rows.append(row)
    # section 3, compact: per check, per (arm, part) block with its groups
    blocks = collections.OrderedDict()
    detail_aside = []
    for f in findings:
        if f["kind"] != "finding" or not f.get("readerRelevant"):
            continue
        if f.get("readerDetail") is False:                  # not a clash between two mods: the evidence index lists it
            detail_aside.append(f)
            continue
        cb = blocks.setdefault(f["checkId"], {"checkId": f["checkId"], "title": f.get("title"),
                                              "check": (plan["scope"].get("checks") or {}).get(f["checkId"]) or {},
                                              "parts": collections.OrderedDict()})
        pk = (f.get("armId"), f.get("part"))
        part = cb["parts"].setdefault(pk, {"armId": f.get("armId"), "part": f.get("part"), "findingsInPart": f["counts"].get("findingsInPart"),
                                           "verdict": f.get("verdict"), "strength": f.get("strength"), "evidenceIds": f.get("evidenceIds") or [],
                                           "indexOnly": (mode != "exhaustive" and f.get("part") == "reverse" and reverse_rule == "index-only"),
                                           "population": f["counts"].get("population"), "state": f["counts"].get("state"),
                                           "groups": [], "rowsTotal": 0})
        part["groups"].append(f)
        part["rowsTotal"] += f["rowsTotal"]
    finding_blocks = []
    arm_kinds = {a["armId"]: a.get("kind") for a in plan["scope"]["arms"]}
    conc = pr.get("concise") or {}
    phrases = (pr.get("reader") or {}).get("kindPhrases") or {}
    compact_over = int(conc.get("compactGroupsOver", 3))
    for cb in blocks.values():
        parts = sorted(cb["parts"].values(), key=lambda x: (arm_sort_key(x["armId"] or "", arm_kinds), PART_ORDER.get(x["part"], 9)))
        if mode != "exhaustive":
            for part in parts:
                # P53: a setup's many pair lines become one line, each pair with its count and its evidence link; the
                # lost edits a declared patch wins are counted once as the patches' own choices. The evidence index
                # keeps every group in full.
                choices = [f for f in part["groups"] if f.get("readerPatchChoice") and f.get("readerRelevant")]
                rest = [f for f in part["groups"] if f not in choices]
                compact = []
                if choices:
                    compact.append({"id": choices[0]["id"], "readerArmLabel": choices[0].get("readerArmLabel"),
                                    "text": bind(conc.get("patchChoicesCollapsed") or "{count} record fields in {pairs} pairs where a patch wins", {
                                        "count": fmt_n(sum(int(f.get("rowsTotal") or 0) for f in choices)),
                                        "pairs": fmt_n(len(choices)),
                                        "fieldNoun": "field" if sum(int(f.get("rowsTotal") or 0) for f in choices) == 1 else "fields",
                                        "pairNoun": "pair" if len(choices) == 1 else "pairs"}),
                                    "ids": [f["id"] for f in choices]})
                relevant = [f for f in rest if f.get("readerRelevant")]
                if len(relevant) > compact_over:
                    entries = []
                    for f in relevant:
                        c = f.get("counts") or {}
                        ph = phrases.get(str(c.get("groupKind") or "")) or {}
                        n_ = int(f.get("rowsTotal") or 0)
                        unit = (ph.get("unitSingular") if n_ == 1 else ph.get("unitPlural")) or ("record" if n_ == 1 else "records")
                        entries.append({"id": f["id"], "text": f"{fmt_n(n_)} {unit} between {c.get('A')} and {c.get('B')}"})
                    compact.append({"id": relevant[0]["id"], "readerArmLabel": relevant[0].get("readerArmLabel"),
                                    "entries": entries,
                                    "note": next((f.get("readerProvenance") for f in relevant if f.get("readerProvenance")), None)})
                    rest = [f for f in rest if not f.get("readerRelevant")]
                part["groups"] = rest
                part["compact"] = compact
        finding_blocks.append({**cb, "parts": parts})
    view = dict(plan)
    view["missingRows"] = missing_rows
    view["findingBlocks"] = finding_blocks
    view["detailAside"] = detail_aside
    view["unjudgeable"] = [f for f in findings if f["kind"] == "unassessed" and f.get("area") == "unjudgeable"]
    view["unassessedOther"] = [f for f in findings if f["kind"] == "unassessed" and f.get("area") not in ("unjudgeable", "media")]
    view["findings"] = findings
    view["sections"] = sections
    view["omissions"] = omissions
    view["effective"] = {"layout": layout, "rowsPerGroup": rows_per_group, "figuresPerFinding": figures_per_finding}
    view["layout"] = layout
    view["evidenceTokensCompact"] = (pr.get("concise") or {}).get("evidenceTokensCompact", 3)
    view["mediaAssessed"] = [m for m in plan.get("media") or [] if m.get("assessed")]
    view["mediaUnassessed"] = [m for m in plan.get("media") or [] if not m.get("assessed")]
    view["assertedSentence"] = pr.get("mediaAssertedSentence")
    return view


def copy_media(plan: dict, bundle_dir: Path, out: Path) -> list[dict]:
    """Copy every assessed media file the report may show under OUT/media/, by content hash - so a
    relocated OUT still resolves its figures. Returns the referenced-media rows for the manifest."""
    rows = []
    for m in plan.get("media") or []:
        if not m.get("assessed") or not m.get("path"):
            continue
        src = bundle_dir / m["path"]
        if not src.exists():
            m["reportPath"] = None
            m["reportNote"] = "media file absent from the bundle at render time"
            continue
        dest = out / "media" / Path(m["path"]).name
        dest.parent.mkdir(parents=True, exist_ok=True)
        if not dest.exists() or sha256_file(dest) != m.get("sha256"):
            shutil.copy2(src, dest)
        m["reportPath"] = f"media/{dest.name}"
        rows.append({"id": m["id"], "path": m["reportPath"], "sha256": sha256_file(dest), "bundlePath": m["path"]})
    return rows


def copy_supporting_evidence(plan: dict, presentation_dir: Path, out: Path) -> list[dict]:
    rows = []
    for e in plan.get("evidence") or []:
        if e.get("storage") != "presentation" or not e.get("path"):
            continue
        src = presentation_dir / e["path"]
        if not src.exists() or sha256_file(src) != e.get("sha256"):
            raise ValueError(f"supporting-evidence-changed: {e['path']} is absent or does not match its recorded sha256")
        dest = out / e["path"]
        dest.parent.mkdir(parents=True, exist_ok=True)
        if src.resolve() != dest.resolve():
            shutil.copy2(src, dest)
        rows.append({"id": e.get("id"), "path": e["path"], "sha256": e["sha256"], "bytes": dest.stat().st_size})
    return rows


def render_all(plan: dict, view: dict, out: Path, css: str) -> dict:
    e_md = env(False)
    e_html = env(True)
    ctx = {"p": view, "css": css}
    md = e_md.get_template("report.md.j2").render(**ctx)
    html = e_html.get_template("report.html.j2").render(**ctx)
    idx = e_md.get_template("evidence-index.md.j2").render(**ctx)
    # the same index as a browser can read: report.html links into THIS one, report.md into the Markdown one.
    # One template per format, as the report itself has - not a converter, so every sentence stays a template
    # sentence bound to bundle values.
    idx_html = e_html.get_template("evidence-index.html.j2").render(**ctx)
    write_text(out / "report.md", md)
    write_text(out / "report.html", html)
    write_text(out / "evidence-index.md", idx)
    write_text(out / "evidence-index.html", idx_html)
    return {"report.md": md, "report.html": html, "evidence-index.md": idx, "evidence-index.html": idx_html}


# -------------------------------------------------------------------------------------- commands --

def cmd_plan(a) -> int:
    pr = load_priorities()
    bundle = Path(a.bundle)
    out = Path(a.out)
    # A flag first, then the saved evaluation settings (evalsettings.py), then the skill's data.
    try:
        s = evalsettings.tool_defaults(["report.mode", "report.maxPages", "report.rowsPerGroup",
                                        "report.figuresPerFinding"])
    except evalsettings.SettingsError as ex:
        print(f"REFUSED: {ex}", file=sys.stderr)
        return EXIT_REFUSED_INPUT
    if getattr(a, "exhaustive", False):
        a.mode = "exhaustive"
    if a.mode is None:
        a.mode = s["report.mode"] or "concise"
    for attr, key in (("max_pages", "report.maxPages"), ("rows_per_group", "report.rowsPerGroup"),
                      ("figures_per_finding", "report.figuresPerFinding")):
        if getattr(a, attr) is None and s[key] is not None:
            setattr(a, attr, int(s[key]))
    with Run("evalreport-plan", stage_source="counted") as r:
        r.stage("verify-bundle", total=1, unit="bundles")
        if not (bundle / "manifest.json").exists():
            return refuse(r, f"bundle-missing: {bundle / 'manifest.json'} does not exist")
        manifest = load_json(bundle / "manifest.json")
        if not isinstance(manifest, dict):
            return refuse(r, f"manifest-unreadable: {bundle / 'manifest.json'} is not JSON")
        if manifest.get("bundleSchema") != BUNDLE_SCHEMA:
            return refuse(r, f"schema-unsupported: bundleSchema {manifest.get('bundleSchema')!r} is not {BUNDLE_SCHEMA}; "
                             "this bundle predates evidence-chain verification - rebuild it from its run")
        out.mkdir(parents=True, exist_ok=True)
        verify, code, err = run_verify_bundle(bundle, out, a.verify_copy)
        if verify is None:
            return refuse(r, f"verify-failed: {err}")
        bl = verify.get("bundle") or {}
        if not bl.get("schemaSupported", True):
            return refuse(r, "schema-unsupported: " + "; ".join(d.get("detail", "") for d in bl.get("defects") or []))
        if not bl.get("complete"):
            defects = bl.get("defects") or []
            names = ", ".join(f"{d.get('kind')} ({d.get('path')})" for d in defects[:8]) or "unnamed defect"
            more = f" and {len(defects) - 8} more" if len(defects) > 8 else ""
            return refuse(r, f"bundle-refused: verify-bundle found the bundle incomplete: {names}{more}; nothing is compiled "
                             f"from a bundle whose evidence does not hash to what was measured")
        bundle_plan = load_json(bundle / manifest["plan"])
        plan_arms = {x.get("armId"): x for x in bundle_plan.get("arms") or []}
        location_scopes = {}
        requested_scopes = list(getattr(a, "location_scope", None) or [])
        normal_scope = load_json(bundle / manifest["scope"]) if manifest.get("scope") else None
        if isinstance(normal_scope, dict) and normal_scope.get("locationContributors"):
            requested_scopes.insert(0, "baseline=" + str(bundle / manifest["scope"]))
        for binding in requested_scopes:
            if "=" not in binding:
                return refuse(r, f"location-scope-invalid: {binding!r} must be ARM=FILE")
            arm_id, source = binding.split("=", 1)
            if arm_id not in plan_arms:
                return refuse(r, f"location-scope-arm-unknown: {arm_id!r} is not a planned arm")
            doc = load_json(Path(source))
            if not isinstance(doc, dict) or doc.get("probe") != "scopeprobe" or not isinstance(doc.get("locationContributors"), dict):
                return refuse(r, f"location-scope-invalid: {source} is not a scopeprobe document with locationContributors")
            req_plugins = [(p.get("name") or p.get("Name"), p.get("sha256") or p.get("Sha256"))
                           for p in ((plan_arms[arm_id].get("requested") or {}).get("plugins") or [])]
            got_plugins = [(p.get("name") or p.get("Name"), p.get("sha256") or p.get("Sha256"))
                           for p in ((doc.get("configuration") or {}).get("plugins") or [])]
            if req_plugins != got_plugins:
                return refuse(r, f"location-scope-foreign: {source} plugin order or hashes do not match arm {arm_id}")
            requested = plan_arms[arm_id].get("requested") or {}
            config = doc.get("configuration") or {}
            def named(rows):
                return [(x.get("name") or x.get("Name"), x.get("kind") or x.get("Kind"),
                         x.get("sha256") or x.get("Sha256")) for x in rows or []]
            if named(requested.get("providers")) != named(config.get("providers")):
                return refuse(r, f"location-scope-foreign: {source} providers do not match arm {arm_id}")
            if named(requested.get("overlays")) != named(config.get("overlays")):
                return refuse(r, f"location-scope-foreign: {source} overlays do not match arm {arm_id}")
            if requested.get("population") not in (None, config.get("population")):
                return refuse(r, f"location-scope-foreign: {source} population does not match arm {arm_id}")
            requested_states = requested.get("states") or []
            if requested_states and config.get("state") not in requested_states:
                return refuse(r, f"location-scope-foreign: {source} state does not match arm {arm_id}")
            location_scopes[arm_id] = doc
        r.advance(1)
        r.stage("compile", total=1, unit="plans")
        conc = pr.get("concise") or {}
        page = pr.get("page") or {}
        settings = {"mode": a.mode, "layout": conc.get("layout", "normal"),
                    "maxPages": (a.max_pages if a.max_pages is not None else conc.get("maxPages", 8)),
                    "page": {"size": page.get("size", "A4"), "marginMm": page.get("marginMm", 16), "baseFontPt": page.get("baseFontPt", 10.5)},
                    "declaredPatches": list(a.declare_patch or []),
                    "rowsPerGroup": (a.rows_per_group if a.rows_per_group is not None else conc.get("rowsPerGroup", 3)),
                    "figuresPerFinding": (a.figures_per_finding if a.figures_per_finding is not None else conc.get("figuresPerFinding", 1)),
                    "reel": {k: (pr.get("reel") or {}).get(k) for k in ("maxSeconds", "fps", "width", "height")},
                    "singleMod": a.single_mod, "headlineUnit": a.headline_unit}
        if a.mode == "exhaustive":
            settings["maxPages"] = None
        sibling = Path(a.sibling_bundle) if getattr(a, "sibling_bundle", None) else None
        if sibling is not None:
            sman = load_json(sibling / "manifest.json")
            if not isinstance(sman, dict) or sman.get("bundleSchema") != BUNDLE_SCHEMA:
                return refuse(r, f"sibling-unreadable: {sibling / 'manifest.json'} is not a schema-{BUNDLE_SCHEMA} bundle "
                                 f"manifest, so nothing was joined.")
            sdir = out / "sibling-verify"
            sdir.mkdir(parents=True, exist_ok=True)
            sverify, _, serr = run_verify_bundle(sibling, sdir, a.verify_copy)
            if sverify is None or not (sverify.get("bundle") or {}).get("complete"):
                return refuse(r, f"sibling-refused: verify-bundle did not find the sibling bundle complete "
                                 f"({serr or 'see sibling-verify/verify.json'}), so nothing was joined.")
        try:
            plan = compile_plan(bundle, verify, settings, pr, str(a.bundle), location_scopes,
                                sibling_dir=sibling, sibling_as_given=getattr(a, "sibling_bundle", None))
        except InputRefused as ex:
            return refuse(r, str(ex))
        for arm_id, doc in location_scopes.items():
            dest = out / "evidence" / f"location-scope-{arm_id}.json"
            write_json(dest, doc)
            location_sha = sha256_file(dest)
            plan["evidence"].append({"id": "E-" + location_sha[:16], "kind": "scope",
                                     "path": dest.relative_to(out).as_posix(), "sha256": location_sha,
                                     "bytes": dest.stat().st_size, "checkId": None, "armId": arm_id,
                                     "part": None, "strength": "measured-scope", "recordedBy": "scopeprobe",
                                     "storage": "presentation"})
        stamp(plan)
        write_json(out / "presentation.json", plan)
        r.artifact(str(out / "presentation.json"))
        r.feature("findings", len(plan["findings"]), "findings")
        r.feature("groups", len(plan["groups"]), "groups")
        r.feature("media", len(plan["media"]), "media")
        r.feature("evidence", len(plan["evidence"]), "files")
        r.feature("imports", len((plan["imports"] or {}).get("rows") or []), "imports")
        r.advance(1)
        print(f"presentation {plan['presentationId']}: {len(plan['findings'])} findings, {len(plan['groups'])} groups, "
              f"{len(plan['media'])} media rows, {len(plan['evidence'])} evidence files; mode {a.mode}; "
              f"bundle complete {plan['source']['verify']['bundleComplete']}, evaluation complete "
              f"{plan['source']['verify']['evaluationComplete']} ({plan['source']['verify']['claim']})")
        print(f"wrote {out / 'presentation.json'}")
    return EXIT_OK


def cmd_triage(a) -> int:
    """Render the version-bound overlap incident catalogue via the report entry point."""
    source = Path(a.manifest)
    out = Path(a.out)
    with Run("evalreport-triage", stage_source="counted") as r:
        r.stage("compile", total=1, unit="catalogues")
        manifest = load_json(source)
        policy = load_json(Path(a.policy))
        if not isinstance(manifest, dict):
            return refuse(r, f"triage-manifest-unreadable: {source} is absent or not JSON")
        if not isinstance(policy, dict):
            return refuse(r, f"triage-policy-unreadable: {a.policy} is absent or not JSON")
        source_sha = sha256_file(source)
        try:
            catalogue, rendered = compile_triage_catalogue(manifest, policy, source_sha, str(source.resolve()), str(out.resolve()))
        except ValueError as exc:
            return refuse(r, str(exc))
        out.mkdir(parents=True, exist_ok=True)
        write_json(out / "overlap-triage-catalogue.json", catalogue)
        write_text(out / "overlap-triage-catalogue.html", rendered)
        detail_pages = render_triage_incident_pages(catalogue)
        for name, content in detail_pages.items():
            write_text(out / name, content)
        r.artifact(str(out / "overlap-triage-catalogue.json"))
        r.artifact(str(out / "overlap-triage-catalogue.html"))
        for name in detail_pages:
            r.artifact(str(out / name))
        r.feature("incidents", len(catalogue["incidents"]), "incidents")
        r.feature("pairs", int(catalogue["accounting"].get("pairsTotal") or 0), "pairs")
        r.advance(1)
        print(f"triage catalogue {catalogue['identity']}: {len(catalogue['incidents'])} incidents; wrote {out}")
    return EXIT_OK


def load_presentation(path: Path):
    plan = load_json(path)
    if not isinstance(plan, dict):
        return None, f"presentation-unreadable: {path} is absent or not JSON"
    if plan.get("schemaVersion") != PRESENTATION_SCHEMA:
        return None, f"presentation-schema-unsupported: schemaVersion {plan.get('schemaVersion')!r} is not {PRESENTATION_SCHEMA}"
    for k in ("findings", "settings", "source", "scope", "evidence"):
        if k not in plan:
            return None, f"presentation-incomplete: no `{k}`"
    if (not isinstance(plan.get("reader"), dict) or "sectionHeadings" not in plan["reader"]
            or any(isinstance(a, dict) and "readerLabel" not in a for a in (plan.get("scope") or {}).get("arms") or [])):
        return None, "presentation-needs-replan: run evalreport plan again to add the modder-facing summary"
    if not all(isinstance(plan["reader"].get(k), dict) for k in ("prediction", "coverage", "validation")):
        return None, ("presentation-needs-replan: run evalreport plan again so the report leads with what adding "
                      "the mods will break")
    if not isinstance(plan.get("imports"), dict):
        # a plan made before imported evidence was described cannot say whether a result was measured for this
        # evaluation, and a report that stays silent about that would read as if it had been
        return None, ("presentation-needs-replan: run evalreport plan again to record where imported evidence "
                      "came from")
    return plan, ""


def cmd_report(a) -> int:
    try:
        if not a.no_print and evalsettings.tool_defaults(["report.pdf"])["report.pdf"] == "off":
            a.no_print = "settings"
    except evalsettings.SettingsError as ex:
        print(f"REFUSED: {ex}", file=sys.stderr)
        return EXIT_REFUSED_INPUT
    pr = load_priorities()
    ppath = Path(a.presentation)
    out = Path(a.out)
    with Run("evalreport-report", stage_source="counted") as r:
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        bundle_dir = Path(plan["source"]["bundleDir"])
        if not bundle_dir.is_absolute():
            bundle_dir = (ppath.parent / bundle_dir).resolve() if not bundle_dir.exists() else bundle_dir
        out.mkdir(parents=True, exist_ok=True)
        try:
            supporting_rows = copy_supporting_evidence(plan, ppath.parent, out)
        except ValueError as ex:
            return refuse(r, str(ex))
        css = read_text(TEMPLATES / "report.css") if (TEMPLATES / "report.css").exists() else ""
        settings = plan["settings"]
        mode = settings.get("mode", "concise")
        ladder = pr.get("abridgementLadder") or [{"step": 0, "layout": settings.get("layout", "normal"),
                                                  "rowsPerGroup": settings.get("rowsPerGroup", 3),
                                                  "figuresPerFinding": settings.get("figuresPerFinding", 1)}]
        # the ladder starts at the plan's own settings and only descends
        steps = [s for s in ladder if s["rowsPerGroup"] <= settings.get("rowsPerGroup", 3) and
                 s["figuresPerFinding"] <= settings.get("figuresPerFinding", 1)] or ladder
        if mode == "exhaustive":
            steps = [{"step": 0, "layout": settings.get("layout", "normal"), "rowsPerGroup": 0, "figuresPerFinding": 10 ** 6}]
        assessed_ids = {m["id"] for m in plan.get("media") or [] if m.get("assessed")}
        has_figures = any(mid in assessed_ids for f in plan["findings"] for mid in (f.get("mediaIds") or []))
        r.feature("findings", len(plan["findings"]), "findings")
        r.feature("media", len(assessed_ids), "media")
        media_rows = copy_media(plan, bundle_dir, out)
        browser, version, why = (None, "", "printing turned off in the evaluation settings (report.pdf off)" if a.no_print == "settings" else "printing disabled by --no-print") if a.no_print else resolve_browser()
        print_info = {"browser": str(browser) if browser else None, "version": version or None, "pages": None,
                      "maxPages": settings.get("maxPages"), "status": "not-rendered", "reason": why or None,
                      "ladder": [], "rootCount": None, "kept": None}
        limit = settings.get("maxPages")
        view = None
        rendered = 0

        def step_row(step, pages=None, note=None):
            return {"step": step.get("step"), "layout": step.get("layout", "normal"), "rowsPerGroup": step["rowsPerGroup"],
                    "figuresPerFinding": step["figuresPerFinding"], "pages": pages, "note": note}

        def render_step(step):
            nonlocal rendered
            v = effective_view(plan, step["rowsPerGroup"], step["figuresPerFinding"], pr, step.get("layout", "normal"))
            rendered += len(render_all(plan, v, out, css))
            r.feature("figures", sum(len(f.get("figures") or []) for f in v["findings"]), "figures")
            return v

        def print_step():
            pdf = out / "report.pdf"
            if pdf.exists():
                pdf.unlink()
            ok, perr = print_pdf(browser, out / "report.html", pdf)
            if not ok:
                return None, None, perr
            return pdf_page_count(pdf)

        r.stage("print", total=len(steps), unit="attempts")
        prev_figs = None
        for step in steps:
            if prev_figs is not None and step["figuresPerFinding"] < prev_figs and not has_figures:
                print_info["ladder"].append(step_row(step, None, "skipped: no figure to drop"))
                r.advance(1)
                continue
            prev_figs = step["figuresPerFinding"]
            view = render_step(step)
            if browser is None:
                r.advance(1)
                break
            pages, root, note = print_step()
            r.advance(1)
            if pages is None and root is None and note and not note.startswith("no page objects"):
                print_info.update({"status": "not-rendered", "reason": note, "pages": None})
                r.note(f"print failed: {note}")
                break
            print_info["rootCount"] = root
            print_info["ladder"].append(step_row(step, pages, note))
            if pages is None or note != "ok":
                print_info.update({"status": "count-unverified", "reason": note, "pages": pages})
                break
            print_info["pages"] = pages
            print_info["reason"] = None
            print_info["kept"] = step_row(step, pages, "kept")
            if mode == "exhaustive" or limit is None:
                print_info["status"] = "rendered"
                break
            if pages <= limit:
                print_info["status"] = "within-limit"
                break
            print_info["status"] = "over-limit"
        if print_info["status"] == "over-limit":
            # A-9 / A-11: a report over the limit either way keeps its pictures, and an abridgement that does not
            # buy the limit is pure loss - the kept rendition is the LEAST abridged step that loses no content:
            # the compact layout (chrome, not content) at the setting's own rows per group and figures per
            # finding; the normal-layout step when the ladder has no compact one. Every step stays in the ladder.
            counted = [l for l in print_info["ladder"] if l.get("pages") is not None]
            full = [l for l in counted if l["rowsPerGroup"] >= settings.get("rowsPerGroup", 3)
                    and l["figuresPerFinding"] >= settings.get("figuresPerFinding", 1)]
            compact_full = [l for l in full if l["layout"] == "compact"]
            best = min(compact_full or full or counted, key=lambda l: l["step"])
            print_info["reason"] = (f"the limit of {limit} page(s) was unreachable without hiding required content; "
                                    "pages at each step: " + ", ".join(f"{l['layout']}/{l['rowsPerGroup']}/{l['figuresPerFinding']} "
                                                                       f"{l['pages']}" for l in counted))
            if best["step"] != (print_info["kept"] or {}).get("step"):
                bstep = next(x for x in steps if x.get("step") == best["step"])
                view = render_step(bstep)
                pages, root, note = print_step()
                print_info["ladder"].append(step_row(bstep, pages, f"{note}; re-rendered as the kept step"))
                best = {**best, "pages": pages if pages is not None else best["pages"]}
            print_info["pages"] = best["pages"]
            print_info["kept"] = {**best, "note": "kept"}
        # ------- manifest
        produced = []
        for name in ARTIFACTS:
            p = out / name
            if p.exists():
                produced.append({"path": name, "sha256": sha256_file(p), "bytes": p.stat().st_size})
        effective = view["effective"] if view else {"layout": settings.get("layout", "normal"), "rowsPerGroup": settings.get("rowsPerGroup"),
                                                    "figuresPerFinding": settings.get("figuresPerFinding")}
        r.note(f"rendered {rendered} rendition(s) over {len(print_info['ladder']) or 1} ladder step(s)")
        manifest = {"schemaVersion": 1, "presentationId": plan.get("presentationId"), "generatedUtc": utc_now(),
                    "source": plan["source"], "compiler": plan["compiler"], "settings": settings, "effective": effective,
                    "artifacts": produced, "media": media_rows,
                    "supportingEvidence": supporting_rows,
                    "omissions": view["omissions"] if view else plan.get("omissions"),
                    "print": print_info}
        write_json(out / "presentation-manifest.json", manifest)
        for x in produced:
            r.artifact(str(out / x["path"]))
        r.artifact(str(out / "presentation-manifest.json"))
        if print_info.get("pages") is not None:
            r.feature("pages", print_info["pages"], "pages", at="final")
        st = print_info["status"]
        print(f"report: {', '.join(x['path'] for x in produced)}; print {st}" +
              (f" ({print_info['pages']} page(s), limit {print_info['maxPages']})" if print_info.get("pages") is not None else
               f" ({print_info.get('reason')})") +
              f"; layout {effective['layout']}, rows per group {effective['rowsPerGroup']}, figures per finding {effective['figuresPerFinding']}"
              + (f"; ladder {[(l['layout'], l['rowsPerGroup'], l['figuresPerFinding'], l['pages']) for l in print_info['ladder']]}" if print_info['ladder'] else ""))
        if st == "over-limit":
            print(f"over-limit: the abridgement ladder ended at {print_info['pages']} page(s) against a limit of "
                  f"{print_info['maxPages']}; the font was not reduced. Use --mode exhaustive for an unbounded rendition.")
        if st == "not-rendered":
            print(f"not-rendered: {print_info.get('reason')}; report.md and report.html were written, no page count is claimed.")
    return EXIT_OK


LINK_MD = re.compile(r"\]\(([^)\s]+)\)")
LINK_HTML = re.compile(r"(?:href|src)=\"([^\"]+)\"")


def check_links(out: Path, name: str, text: str, defects: list, counter: dict) -> None:
    pat = LINK_HTML if name.endswith(".html") else LINK_MD
    anchors_cache: dict[str, set] = {}
    for m in pat.finditer(text):
        target = m.group(1)
        if re.match(r"^[a-z]+:", target) or target.startswith("data:"):
            continue
        path, _, frag = target.partition("#")
        counter["links"] += 1
        if path:
            fp = out / path
            if not fp.exists():
                defects.append({"kind": "link-unresolved", "path": name, "detail": f"{target} does not resolve from {out}"})
                continue
        else:
            fp = out / name
        if frag:
            key = str(fp)
            if key not in anchors_cache:
                t = read_text(fp) if fp.exists() else ""
                anchors_cache[key] = set(re.findall(r"id=\"([^\"]+)\"", t))
            if frag not in anchors_cache[key]:
                defects.append({"kind": "anchor-unresolved", "path": name, "detail": f"{target}: no anchor `{frag}` in {fp.name}"})


def cmd_verify(a) -> int:
    out = Path(a.out)
    with Run("evalreport-verify", stage_source="counted") as r:
        man = load_json(out / "presentation-manifest.json")
        if not isinstance(man, dict):
            return refuse(r, f"manifest-missing: {out / 'presentation-manifest.json'} is absent or not JSON")
        plan, err = load_presentation(out / "presentation.json")
        if plan is None:
            return refuse(r, err)
        defects = []
        counter = {"links": 0, "artifacts": 0, "media": 0, "supportingEvidence": 0}
        arts = man.get("artifacts") or []
        r.stage("verify", total=len(arts) + len(man.get("media") or []) + len(man.get("supportingEvidence") or []) + 1, unit="checks")
        if plan.get("presentationId") != man.get("presentationId"):
            defects.append({"kind": "presentation-mismatch", "path": "presentation.json",
                            "detail": f"presentationId {plan.get('presentationId')} is not the manifest's {man.get('presentationId')}"})
        for x in arts:
            p = out / x["path"]
            counter["artifacts"] += 1
            if not p.exists():
                defects.append({"kind": "artifact-missing", "path": x["path"], "detail": "listed in the manifest, absent on disk"})
            elif sha256_file(p) != x.get("sha256"):
                defects.append({"kind": "artifact-changed", "path": x["path"], "detail": "does not hash to the manifest's sha256"})
            elif x["path"].endswith((".md", ".html")):
                check_links(out, x["path"], read_text(p), defects, counter)
            r.advance(1)
        for m in man.get("media") or []:
            p = out / m["path"]
            counter["media"] += 1
            if not p.exists():
                defects.append({"kind": "media-missing", "path": m["path"], "detail": "referenced media absent"})
            elif sha256_file(p) != m.get("sha256"):
                defects.append({"kind": "media-changed", "path": m["path"], "detail": "does not hash to the manifest's sha256"})
            r.advance(1)
        for e in man.get("supportingEvidence") or []:
            p = out / e["path"]
            counter["supportingEvidence"] += 1
            if not p.exists():
                defects.append({"kind": "supporting-evidence-missing", "path": e["path"], "detail": "listed in the manifest, absent on disk"})
            elif sha256_file(p) != e.get("sha256"):
                defects.append({"kind": "supporting-evidence-changed", "path": e["path"], "detail": "does not hash to the manifest's sha256"})
            r.advance(1)
        pi = man.get("print") or {}
        pages_now = None
        if pi.get("status") in ("within-limit", "over-limit", "rendered", "count-unverified"):
            if not (out / "report.pdf").exists():
                defects.append({"kind": "pdf-missing", "path": "report.pdf", "detail": f"print status {pi.get('status')} but no PDF"})
            else:
                pages_now, root, note = pdf_page_count(out / "report.pdf")
                if pages_now != pi.get("pages"):
                    defects.append({"kind": "page-count-mismatch", "path": "report.pdf",
                                    "detail": f"recomputed {pages_now} page(s) ({note}); the manifest says {pi.get('pages')}"})
                if pi.get("status") == "within-limit" and pi.get("maxPages") is not None and (pages_now or 0) > pi["maxPages"]:
                    defects.append({"kind": "limit-contradiction", "path": "report.pdf",
                                    "detail": f"{pages_now} page(s) exceed the limit {pi['maxPages']} yet the status is within-limit"})
                if pi.get("status") == "over-limit":
                    # a report over its page limit fails its own acceptance: verify never says ok for it
                    defects.append({"kind": "over-limit", "path": "report.pdf",
                                    "detail": f"{pages_now} page(s) against a limit of {pi.get('maxPages')}"})
        r.advance(1)
        result = {"schemaVersion": 1, "checkedUtc": utc_now(), "out": str(out), "presentationId": plan.get("presentationId"),
                  "ok": not defects, "artifacts": counter["artifacts"], "media": counter["media"],
                  "supportingEvidence": counter["supportingEvidence"], "links": counter["links"],
                  "pages": pages_now, "printStatus": pi.get("status"), "defects": defects}
        write_json(out / "presentation-verify.json", result)
        r.feature("links", counter["links"], "links")
        if pages_now is not None:
            r.feature("pages", pages_now, "pages", at="final")
        if defects:
            for d in defects:
                print(f"DEFECT {d['kind']} {d['path']}: {d['detail']}", file=sys.stderr)
            return refuse(r, f"presentation-refused: {len(defects)} defect(s) - see presentation-verify.json", EXIT_REFUSED_RESULT)
        print(f"verified: {counter['artifacts']} artifact(s), {counter['media']} media file(s), {counter['links']} link(s) resolve"
              + (f", {pages_now} page(s) ({pi.get('status')})" if pages_now is not None else f", print {pi.get('status')}"))
    return EXIT_OK


# ------------------------------------------------------------------------------- narration seam --

NUM = re.compile(r"(?<![\w:./-])\d[\d,]*(?:\.\d+)?(?![\w:./-])")
CHECK_TOKEN = re.compile(r"\b[a-z]+\.[a-z][a-z-]+\b")
ARM_TOKEN = re.compile(r"\b(?:candidate|supplied)-\d\d[\w-]*")
PLUGIN_TOKEN = re.compile(r"[A-Za-z0-9_'()\-]+(?: [A-Za-z0-9_'()\-]+)*\.(?:esp|esm|esl)\b", re.I)
FORMKEY = re.compile(r"\b[0-9A-Fa-f]{6}:[^\s,;()]+\.(?:esp|esm|esl)\b", re.I)


def bound_sets(plan: dict, f: dict) -> tuple[set, set]:
    nums = set()

    def walk(v):
        if isinstance(v, bool):
            return
        if isinstance(v, (int, float)):
            nums.add(str(int(v)) if float(v).is_integer() else str(v))
        elif isinstance(v, dict):
            for x in v.values():
                walk(x)
        elif isinstance(v, list):
            for x in v:
                walk(x)
        elif isinstance(v, str):
            for n in NUM.findall(v):
                nums.add(n.replace(",", ""))
    walk(f.get("counts"))
    for row in f.get("allRows") or f.get("rows") or []:
        walk({k: row.get(k) for k in ("summary", "physicalDetail", "label")})
    names = set()
    c = f.get("counts") or {}
    for k in ("A", "B", "probe"):
        if c.get(k):
            names.add(str(c[k]))
    for g in c.get("groups") or []:
        for k in ("A", "B"):
            if g.get(k):
                names.add(str(g[k]))
    for row in f.get("allRows") or f.get("rows") or []:
        for k in ("formKey", "label", "summary"):
            if row.get(k):
                names.add(str(row[k]))
    if f.get("checkId"):
        names.add(f["checkId"])
    if f.get("armId"):
        names.add(f["armId"])
    arm = next((a for a in plan["scope"]["arms"] if a["armId"] == f.get("armId")), None)
    if arm:
        for n in (arm.get("pluginsAdded") or []) + (arm.get("pluginsRemoved") or []):
            names.add(n)
    return nums, names


def universe(plan: dict) -> tuple[set, set, set, set]:
    plugins, formkeys, labels = set(), set(), set()
    for f in plan["findings"]:
        c = f.get("counts") or {}
        for k in ("A", "B"):
            if c.get(k):
                plugins.add(str(c[k]))
        for row in f.get("allRows") or f.get("rows") or []:
            if row.get("formKey"):
                formkeys.add(row["formKey"])
            if row.get("label"):
                labels.add(row["label"])
    for a in plan["scope"]["arms"]:
        for n in (a.get("pluginsAdded") or []) + (a.get("pluginsRemoved") or []):
            plugins.add(n)
    for t in plan["scope"].get("targets") or []:
        if t.get("name"):
            plugins.add(t["name"])
    checks = set(plan["scope"].get("checks") or {}) | {f["checkId"] for f in plan["findings"] if f.get("checkId")}
    return plugins, formkeys, labels, checks


def validate_answer(plan: dict, f: dict, text: str, pr: dict) -> list[dict]:
    """D7: every number bound, every named plugin / cell / formKey bound, no check or arm outside the plan,
    none of the forbidden words. Returns the rule violations (empty = accepted)."""
    bad = []
    nums, names = bound_sets(plan, f)
    plugins, formkeys, labels, checks = universe(plan)
    arms = {a["armId"] for a in plan["scope"]["arms"]}
    low = text.lower()
    for pat in pr.get("forbiddenPatterns") or []:
        m = re.search(pat, low)
        if m:
            bad.append({"rule": "forbidden-word", "detail": f"contains `{m.group(0)}`"})
    for n in NUM.findall(text):
        v = n.replace(",", "")
        if v not in nums:
            bad.append({"rule": "unbound-number", "detail": f"{n} is not one of the finding's bound numbers"})
    for tok in set(CHECK_TOKEN.findall(text)):
        if tok in checks and tok != f.get("checkId"):
            bad.append({"rule": "unbound-check", "detail": f"names check {tok}, which is not this finding's"})
        elif tok not in checks and re.match(r"^(loadability|records|geometry|functional|state|environment|patch|runtime)\.", tok):
            bad.append({"rule": "unknown-check", "detail": f"names {tok}, which is not a check in the plan"})
    for tok in set(ARM_TOKEN.findall(text)):
        if tok not in arms:
            bad.append({"rule": "unknown-arm", "detail": f"names arm {tok}, which is not in the plan"})
        elif tok != f.get("armId"):
            bad.append({"rule": "unbound-arm", "detail": f"names arm {tok}, which is not this finding's"})
    if re.search(r"\bbaseline\b", low) and f.get("armId") not in (None, "baseline") and (f.get("armId") or "").lower() not in low:
        # the baseline is the reference a differential is stated against, so naming it beside the finding's own
        # arm is fine; naming it INSTEAD of that arm places the finding on the wrong arm
        bad.append({"rule": "unbound-arm", "detail": "names the baseline arm without naming this finding's own arm"})
    for p in plugins:
        if p.lower() in low and p not in names:
            bad.append({"rule": "unbound-plugin", "detail": f"names {p}, which is not bound to this finding"})
    masked = text
    for known in sorted(set(names) | set(plugins), key=len, reverse=True):
        if known.lower().endswith((".esp", ".esm", ".esl")):
            masked = re.sub(re.escape(known), " " * len(known), masked, flags=re.I)
    for tok in set(PLUGIN_TOKEN.findall(masked)):
        short = " ".join(tok.split()[-4:])
        bad.append({"rule": "unknown-plugin", "detail": f"names {short}, which is not a plugin bound to this finding"})
    for fk in set(FORMKEY.findall(text)):
        if fk not in names and fk.upper() not in {n.upper() for n in names}:
            bad.append({"rule": "unbound-formkey" if fk in formkeys else "unknown-formkey",
                        "detail": f"names {fk}, which is not bound to this finding"})
    for lab in labels:
        if len(lab) >= 6 and lab.lower() in low and lab not in names:
            bad.append({"rule": "unbound-cell", "detail": f"names {lab}, which is not bound to this finding"})
    return bad


def cmd_narrate_packet(a) -> int:
    ppath = Path(a.presentation)
    with Run("evalreport-narrate", mode="packet", stage_source="counted") as r:
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        r.stage("packet", total=len(plan["findings"]), unit="findings")
        items = []
        for f in plan["findings"]:
            nums, names = bound_sets(plan, f)
            items.append({"id": f["id"], "kind": f["kind"], "checkId": f.get("checkId"), "armId": f.get("armId"), "part": f.get("part"),
                          "title": f.get("title"), "template": f.get("plain"), "counts": f.get("counts"),
                          "boundNumbers": sorted(nums, key=lambda s: (len(s), s)), "boundNames": sorted(names),
                          "rows": (f.get("allRows") or f.get("rows") or [])[: max(1, plan["settings"].get("rowsPerGroup") or 3)]})
            r.advance(1)
        packet = {"schemaVersion": 1, "presentationId": plan.get("presentationId"),
                  "instructions": "Rewrite each finding's `template` sentence in plain language. Use only the numbers in "
                                  "boundNumbers and the names in boundNames; name no other check, arm, plugin, cell or form key; "
                                  "never use the words pass, compatible, clean or verified-in-game; never claim completion. "
                                  "Answer as {\"answers\": [{\"id\": \"F-001\", \"text\": \"...\"}]}. A refused answer keeps the template.",
                  "checks": sorted(plan["scope"].get("checks") or {}), "arms": [x["armId"] for x in plan["scope"]["arms"]],
                  "findings": items}
        write_json(Path(a.out), packet)
        r.artifact(str(a.out))
        r.feature("findings", len(items), "findings")
        print(f"packet: {len(items)} finding(s) -> {a.out}")
    return EXIT_OK


def cmd_narrate_apply(a) -> int:
    pr = load_priorities()
    ppath = Path(a.presentation)
    with Run("evalreport-narrate", mode="apply", stage_source="counted") as r:
        plan, err = load_presentation(ppath)
        if plan is None:
            return refuse(r, err)
        answers = load_json(Path(a.answers))
        if isinstance(answers, dict) and isinstance(answers.get("answers"), dict):
            answers = {**answers, "answers": [{"id": k, "text": v} for k, v in answers["answers"].items()]}
        if not isinstance(answers, dict) or not isinstance(answers.get("answers"), list):
            return refuse(r, f"answers-unreadable: {a.answers} is not a JSON object with an `answers` list "
                             f"([{{id, text}}]) or mapping ({{id: text}})")
        if answers.get("presentationId") not in (None, plan.get("presentationId")):
            return refuse(r, f"answers-foreign: the answers name presentation {answers.get('presentationId')}, this is {plan.get('presentationId')}")
        by_id = {f["id"]: f for f in plan["findings"]}
        r.stage("apply", total=len(answers["answers"]), unit="answers")
        accepted, refused = 0, []
        for ans in answers["answers"]:
            fid = (ans or {}).get("id")
            text = (ans or {}).get("text")
            f = by_id.get(fid)
            if f is None:
                refused.append({"id": fid, "rule": "unknown-finding", "detail": f"{fid} is not in the plan"})
                r.advance(1)
                continue
            if not isinstance(text, str) or not text.strip():
                refused.append({"id": fid, "rule": "empty-answer", "detail": "no text"})
                r.advance(1)
                continue
            bad = validate_answer(plan, f, text, pr)
            if bad:
                for x in bad:
                    refused.append({"id": fid, **x})
            else:
                f.setdefault("templatePlain", f["plain"])
                old_plain = f["plain"]
                f["plain"] = text.strip()
                f["readerPlain"] = text.strip()
                f["narrated"] = True
                for chapter in plan.get("chapters") or []:
                    if f.get("id") not in (chapter.get("findingIds") or []):
                        continue
                    for caption in chapter.get("captions") or []:
                        for key in ("text", "narration"):
                            if old_plain in str(caption.get(key) or ""):
                                caption[key] = str(caption[key]).replace(old_plain, text.strip())
                accepted += 1
            r.advance(1)
        plan["narration"] = {"source": f"answers:{sha256_file(Path(a.answers))[:12]}", "validated": True,
                             "accepted": accepted, "refused": refused}
        dest = Path(a.out) if a.out else ppath
        stamp(plan)
        write_json(dest, plan)
        r.artifact(str(dest))
        r.feature("findings", len(plan["findings"]), "findings")
        r.tally(ok=accepted, failed=len(refused))
        print(f"narration: {accepted} answer(s) accepted, {len(refused)} refusal(s); wrote {dest}")
        for x in refused:
            print(f"  refused {x['id']}: {x['rule']} - {x['detail']}")
    return EXIT_OK


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="evalreport", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("plan", help="verify the bundle and compile the presentation plan")
    p.add_argument("--bundle", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--mode", choices=("concise", "exhaustive"), default=None,
                   help="default: the saved evaluation settings' report.mode, else concise")
    p.add_argument("--exhaustive", action="store_true", help="list every row; no page limit (same as --mode exhaustive)")
    p.add_argument("--max-pages", type=int, default=None)
    p.add_argument("--rows-per-group", type=int, default=None)
    p.add_argument("--figures-per-finding", type=int, default=None)
    p.add_argument("--verify-copy", action="store_true",
                   help="verify a temporary copy of the bundle so the bundle directory itself is never written to")
    p.add_argument("--location-scope", action="append", default=[], metavar="ARM=FILE",
                   help="bind a scopeprobe location-contributor document to an arm; repeat for multiple arms")
    p.add_argument("--single-mod", choices=("show", "aside"), default=None,
                   help="a clash one evaluated mod has with a third mod on its own: shown under its own heading (show) "
                        "or set aside with a count (aside); never counted in the verdict. Default: the reporting "
                        "skill's singleModDefault")
    p.add_argument("--headline-unit", choices=("relationships", "objects"), default=None,
                   help="what the verdict counts: the check's relationships (rows) or the objects they name. Default: "
                        "the reporting skill's headlineUnit")
    p.add_argument("--sibling-bundle", default=None, metavar="DIR",
                   help="a bundle of the same order without the patch; its single-mod setups attribute this report's "
                        "prediction when its setup matches this bundle's setup without the patch plugin for plugin")
    p.add_argument("--declare-patch", action="append", default=[], metavar="PLUGIN",
                   help="declare a plugin as a patch under evaluation; repeat for several. Patch "
                        "identity is never inferred from plugin membership, so an arm that differs "
                        "only by an undeclared plugin is reported as an unstated difference. Use "
                        "this for a plan whose candidates predate the declaration field.")
    p.set_defaults(fn=cmd_plan)
    p = sub.add_parser("triage", help="compile an annotated overlap manifest into a readable incident catalogue")
    p.add_argument("--manifest", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--policy", default=str(TRIAGE_POLICY))
    p.set_defaults(fn=cmd_triage)
    p = sub.add_parser("report", help="render report.md / report.html / report.pdf / evidence-index.md from the plan")
    p.add_argument("--presentation", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--no-print", action="store_true", help="skip the browser; print.status is not-rendered")
    p.set_defaults(fn=cmd_report)
    p = sub.add_parser("verify", help="re-hash the artifacts, resolve every link, recount the pages")
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_verify)
    p = sub.add_parser("narrate-packet", help="write every finding's bound numbers and names for an optional narrator")
    p.add_argument("--presentation", required=True)
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_narrate_packet)
    p = sub.add_parser("narrate-apply", help="accept narrated sentences only where every number and name is bound")
    p.add_argument("--presentation", required=True)
    p.add_argument("--answers", required=True)
    p.add_argument("--out", default=None)
    p.set_defaults(fn=cmd_narrate_apply)
    return ap


def main(argv=None) -> int:
    a = build_parser().parse_args(argv)
    try:
        return a.fn(a)
    except KeyboardInterrupt:
        print("interrupted", file=sys.stderr)
        return EXIT_REFUSED_INPUT


if __name__ == "__main__":
    sys.exit(main())
