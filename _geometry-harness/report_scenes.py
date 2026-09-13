"""Evidence-bound capture requests; domain selection rules live in the reporting skill.

This is planning, not capture or a new detector. Requests carry the original source identity,
both participants and a declared ranking basis. Missing measurements never become zeroes.
"""
from __future__ import annotations

from copy import deepcopy
import hashlib
import json
import math
import re
from pathlib import Path

POLICY = Path(__file__).resolve().parent.parent / ".claude/skills/evaluation-reporting/references/scene-policy.json"


def capture_requests(presentation: dict, bundle: Path, policy_path: Path = POLICY) -> dict:
    policy_bytes = policy_path.read_bytes()
    policy = json.loads(policy_bytes)
    evidence = {e["id"]: e for e in presentation.get("evidence") or []}
    documents, candidates, limits = {}, {}, []
    groups = {}
    for finding in presentation.get("findings") or []:
        if finding.get("kind") not in ("finding", "regression") or finding.get("part") == "reverse" or finding.get("readerRelevant") is False:
            continue
        forms = {r.get("formKey") for r in finding.get("allRows") or []}
        for rule in policy["rules"]:
            if finding.get("checkId") != rule["checkId"]:
                continue
            for eid in finding.get("evidenceIds") or []:
                source = evidence.get(eid, {})
                if source.get("kind") != "emit" or source.get("part") != "arm" or source.get("armId") != finding.get("armId"):
                    continue
                if eid not in documents:
                    path = (bundle / source["path"]).resolve()
                    if not path.is_relative_to(bundle.resolve()):
                        raise ValueError("capture-source-refused: evidence path escapes its bundle")
                    raw = path.read_bytes()
                    if not source.get("sha256") or hashlib.sha256(raw).hexdigest() != source["sha256"]:
                        raise ValueError("capture-source-refused: evidence changed since presentation planning")
                    documents[eid] = json.loads(raw)
                doc = documents[eid]
                if doc.get("Probe") != rule["probe"]:
                    continue
                if not (doc.get("Configuration") or {}).get("Fingerprint"):
                    limits.append({"findingId": finding["id"], "reason": "The source does not identify the measured configuration."})
                    continue
                order = [p.get("Name", "").casefold() for p in (doc.get("Configuration") or {}).get("Plugins") or []]
                for conflict in doc.get("Conflicts") or []:
                    if conflict.get("Kind") != rule["conflictKind"]:
                        continue
                    counts = finding.get("counts") or {}
                    if not rule.get("corroborationKinds") and counts.get("A") and (counts["A"], counts.get("B")) != (conflict.get("A"), conflict.get("B")):
                        continue
                    for record in conflict.get("Necessary") or []:
                        physical = rule["physical"] if isinstance(rule["physical"], list) else [rule["physical"]]
                        if record.get("FormKey") not in forms or record.get("Physical") not in physical:
                            continue
                        corroboration = []
                        if rule.get("corroborationKinds"):
                            # The outer finding must be the measured problem, not the historical overwrite.
                            bound_groups = (counts.get("groups") or []) if finding.get("kind") == "regression" else [
                                {"kind": counts.get("groupKind"), "A": counts.get("A"), "B": counts.get("B")}]
                            bound_keys = {(g.get("kind"), g.get("A"), g.get("B")) for g in bound_groups}
                            for support in doc.get("Conflicts") or []:
                                if support.get("Kind") not in rule["corroborationKinds"] or (
                                        support.get("Kind"), support.get("A"), support.get("B")) not in bound_keys:
                                    continue
                                for witness in support.get("Necessary") or []:
                                    if witness.get("FormKey") == record["FormKey"] and witness.get("Physical") == "Holds":
                                        corroboration.append({"kind": support["Kind"], "formKey": witness["FormKey"],
                                            "otherRecordKeys": [r["FormKey"] for r in support.get("Alternative") or []],
                                            "plugins": [support.get("A"), support.get("B")],
                                            "detail": witness.get("PhysicalDetail"), "evidenceId": eid})
                            if not corroboration:
                                continue
                        key = (finding.get("armId"), rule["requestType"], conflict["A"], conflict["B"])
                        refs = [record["FormKey"]]
                        refs += sorted({key for c in corroboration for key in c["otherRecordKeys"] if key not in refs})
                        highlights = []
                        if rule.get("highlights"):
                            if len(conflict.get("Necessary") or []) != 1 or len(conflict.get("Alternative") or []) != 1:
                                limits.append({"findingId": finding["id"], "reason": "The evidence does not identify one unambiguous object pair."})
                                continue
                            refs += [conflict["Alternative"][0]["FormKey"]]
                            highlights = [{"formKey": conflict[x["recordSet"]][0]["FormKey"], "color": x["color"]} for x in rule["highlights"]]
                        views = []
                        if rule.get("views"):
                            earlier, later = conflict["A"].casefold(), conflict["B"].casefold()
                            if earlier not in order or later not in order or order.index(earlier) >= order.index(later):
                                limits.append({"findingId": finding["id"], "reason": "The recorded order does not establish the requested navmesh-version direction."})
                                continue
                            views = [{"role": v["role"], "navPlugin": conflict[v["pluginField"]], "navColor": v["navColor"], "formKey": refs[0]} for v in rule["views"]]
                        match = re.search(rule["metricPattern"], record.get("PhysicalDetail") or "")
                        values = {field: float(match[field].replace(",", "")) for field in rule["metricFields"]} if match else {}
                        rank = math.prod(values.values()) if values else None
                        if rank is not None and (not math.isfinite(rank) or rank < 0):
                            rank, values = None, {}
                        identity = json.dumps([key, refs, source["sha256"]], separators=(",", ":"))
                        rid = "capture-" + hashlib.sha256(identity.encode()).hexdigest()[:12]
                        if rid in candidates:
                            candidates[rid]["findingIds"] = sorted(set(candidates[rid]["findingIds"] + [finding["id"]]))
                            candidates[rid]["newProblem"] |= finding["kind"] == "regression"
                            continue
                        row = {"id": rid, "requestType": rule["requestType"], "checkId": rule["checkId"],
                               "armId": finding["armId"], "findingIds": [finding["id"]], "newProblem": finding["kind"] == "regression",
                               "location": record.get("Label"), "recordKeys": refs,
                               "plugins": list(dict.fromkeys([conflict["A"], conflict["B"]] +
                                   [name for c in corroboration for name in c["plugins"] if name])), "state": doc.get("State"),
                               "configurationFingerprint": (doc.get("Configuration") or {}).get("Fingerprint"),
                               "source": {"evidenceId": eid, "path": source["path"], "sha256": source["sha256"]},
                               "ranking": {"value": rank, "measurements": values, "basis": rule["metricLabel"], "limit": rule["metricLimit"]},
                               "views": views, "highlights": highlights, "corroboration": corroboration,
                               "explanation": rule["explanation"],
                               "camera": deepcopy({**policy["camera"], **rule.get("camera", {})}),
                               "sequence": deepcopy(rule.get("sequence", {})),
                               "review": deepcopy(rule.get("review", {})),
                               "status": "needs-capture-and-visual-review"}
                        candidates[rid] = row
                        groups.setdefault(key, []).append(rid)
    requests = []
    for key in sorted(groups):
        group = sorted((candidates[rid] for rid in groups[key]), key=lambda r: (not r["newProblem"], r["ranking"]["value"] is None,
                        -(r["ranking"]["value"] or 0), r["id"]))
        selected = group[:policy["examplesPerPair"]]
        for row in selected:
            row["selection"] = {"eligible": len(group), "selected": len(selected), "omitted": len(group)-len(selected),
                                "unranked": sum(r["ranking"]["value"] is None for r in group)}
        requests.extend(selected)
    return {"policySha256": hashlib.sha256(policy_bytes).hexdigest(), "requests": requests,
            "limitations": limits, "note": "A ranked example is a capture request, not qualified media or a severity verdict. Complete capture and visual review before binding it to a scene."}
