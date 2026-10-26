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


COLOURS = ("cyan", "magenta")


def _source(presentation: dict, bundle: Path, finding_id: str | None, want) -> tuple[dict, dict] | None:
    """The finding's own evidence file that `want(path, doc)` accepts, re-hashed against the presentation's record."""
    finding = next((f for f in presentation.get("findings") or [] if f.get("id") == finding_id), None)
    evidence = {e["id"]: e for e in presentation.get("evidence") or []}
    for eid in (finding or {}).get("evidenceIds") or []:
        e = evidence.get(eid) or {}
        if e.get("kind") != "emit" or not str(e.get("path", "")).endswith(".json"):
            continue
        path = (bundle / e["path"]).resolve()
        if not path.is_relative_to(bundle.resolve()):
            raise ValueError("capture-source-refused: evidence path escapes its bundle")
        if not path.is_file():
            continue
        raw = path.read_bytes()
        if not e.get("sha256") or hashlib.sha256(raw).hexdigest() != e["sha256"]:
            raise ValueError("capture-source-refused: evidence changed since presentation planning")
        doc = json.loads(raw)
        if want(e["path"], doc):
            return {"evidenceId": eid, "path": e["path"], "sha256": e["sha256"]}, doc
    return None


def reel_requests(presentation: dict, bundle: Path, policy: dict) -> tuple[list[dict], list[dict]]:
    """The reel's placement splits and world-state vignettes as capture requests: what to show from the chapter, what
    binds it from its finding's own evidence (a placement: the record import's result.json and its configuration; a
    world state: the stateprobe emit's conflict whose Kind the chapter names)."""
    out, limits = [], []
    reel = presentation.get("reel") or {}
    for ch in reel.get("chapters") or []:
        c = ch.get("clash") or {}
        if ch.get("kind") == "split":
            got = _source(presentation, bundle, c.get("findingId"),
                          lambda path, doc: path.endswith("result.json") and bool((doc.get("configuration") or {}).get("fingerprint")))
            if got is None:
                limits.append({"findingId": c.get("findingId"), "reason": "The placement's record import names no configuration."})
                continue
            source, doc = got
            fk = c.get("formKey")
            identity = json.dumps(["placement-split", c.get("armId"), fk, source["sha256"]], separators=(",", ":"))
            out.append({"id": "capture-" + hashlib.sha256(identity.encode()).hexdigest()[:12],
                        "requestType": "placement-split", "checkId": c.get("checkId") or "records.contested-fields",
                        "armId": c.get("armId"), "findingIds": [c.get("findingId")], "newProblem": False,
                        "location": None, "recordKeys": [fk], "plugins": list(c.get("pair") or []),
                        "state": doc.get("state") or "baseline",
                        "configurationFingerprint": doc["configuration"]["fingerprint"], "source": source,
                        "highlights": [{"formKey": fk, "color": COLOURS[0]}], "actor": bool(c.get("actor")),
                        "placements": c.get("placements"), "cells": c.get("cells"), "labels": c.get("labels"),
                        "camera": deepcopy(policy.get("camera") or {}),
                        "sequence": {"states": ["left", "right"],
                                     "setups": {"left": c.get("withoutArmId"), "right": c.get("armId")}},
                        "vignette": {"chapterId": ch.get("id"), "withoutArmId": c.get("withoutArmId"), "states": ["left", "right"]},
                        "selection": {"eligible": 1, "selected": 1, "omitted": 0, "unranked": 1,
                                      "rule": "the reel plan's split selection"},
                        "status": "needs-capture-and-visual-review"})
        elif ch.get("kind") == "vignette" and c.get("type") == "state" and c.get("state"):
            def match(path, doc):
                return doc.get("Probe") in (None, "stateprobe") and any(
                    x.get("Kind") == c.get("kind") or str(x.get("Kind", "")).startswith(f"STATE-") and f"@{c['state']}@" in str(x.get("Kind"))
                    for x in doc.get("Conflicts") or [])
            got = _source(presentation, bundle, c.get("findingId"), match)
            if got is None:
                limits.append({"findingId": c.get("findingId"), "reason": "The world-state clash's stateprobe emit was not found."})
                continue
            source, doc = got
            conflict = next(x for x in doc.get("Conflicts") or []
                            if (x.get("Kind") == c.get("kind") or f"@{c['state']}@" in str(x.get("Kind")))
                            and any(n.get("FormKey") == c.get("formKey") for n in x.get("Necessary") or []))
            pair = [c.get("formKey"), *(c.get("other") or [])][:2]
            marks = [fk for fk in pair if fk in (c.get("highlight") or pair)]
            identity = json.dumps(["state-pair", c.get("armId"), pair, c.get("state"), source["sha256"]], separators=(",", ":"))
            label = ((conflict.get("Necessary") or [{}])[0]).get("Label")
            rule = next((r for r in policy.get("rules") or [] if r.get("checkId") == "geometry.object-overlap"), {})
            out.append({"id": "capture-" + hashlib.sha256(identity.encode()).hexdigest()[:12],
                        "requestType": "state-pair", "checkId": "state.conditional-geometry", "armId": c.get("armId"),
                        "findingIds": [c.get("findingId")], "newProblem": False, "location": label,
                        "recordKeys": pair, "plugins": [conflict.get("A"), conflict.get("B")],
                        "state": c.get("state"), "kind": conflict.get("Kind"),
                        "configurationFingerprint": (doc.get("Configuration") or {}).get("Fingerprint"), "source": source,
                        "highlights": [{"formKey": fk, "color": COLOURS[pair.index(fk)]} for fk in marks],
                        "pointParticipants": [{"formKey": fk, "color": COLOURS[i]} for i, fk in enumerate(pair)],
                        "camera": deepcopy({**(policy.get("camera") or {}), **(rule.get("camera") or {})}),
                        "sequence": {**deepcopy(rule.get("sequence") or {}), "states": list((policy.get("vignettes") or {}).get("sequence") or []),
                                     "setups": {"without": c.get("withoutArmId"), "with": c.get("armId"), "highlight": c.get("armId")}},
                        "vignette": {"chapterId": ch.get("id"), "withoutArmId": c.get("withoutArmId"),
                                     "states": [f.get("state") for f in ch.get("frames") or []]},
                        "selection": {"eligible": 1, "selected": 1, "omitted": 0, "unranked": 1,
                                      "rule": "the reel plan's world-state selection"},
                        "status": "needs-capture-and-visual-review"})
    return out, limits


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
                        pattern = rule.get("metricPattern")
                        match = re.search(pattern, record.get("PhysicalDetail") or "") if pattern else None
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
    reel = presentation.get("reel") if isinstance(presentation.get("reel"), dict) else {}
    if reel.get("status") == "planned":
        # the vignette reel decides what is pictured: exactly its vignettes, one request each, with the setup the
        # "without" frame is rendered from (skill data: the overlap rule's sequence.setups)
        wanted = {}
        for ch in reel.get("chapters") or []:
            c = ch.get("clash") or {}
            if ch.get("kind") == "vignette" and c.get("type") in (None, "overlap", "contact"):
                wanted[(c.get("armId"), c.get("formKey"))] = ch
        for row in sorted(candidates.values(), key=lambda r: r["id"]):
            ch = wanted.get((row["armId"], row["recordKeys"][0]))
            if ch is None:
                continue
            row["vignette"] = {"chapterId": ch.get("id"), "withoutArmId": (ch.get("clash") or {}).get("withoutArmId"),
                               "states": [f.get("state") for f in ch.get("frames") or []]}
            row["selection"] = {"eligible": 1, "selected": 1, "omitted": 0, "unranked": int(row["ranking"]["value"] is None),
                                "rule": "the reel plan's vignette selection"}
            requests.append(row)
        more, more_limits = reel_requests(presentation, bundle, policy)
        requests += more
        limits += more_limits
        return {"policySha256": hashlib.sha256(policy_bytes).hexdigest(), "requests": requests, "limitations": limits,
                "note": "One request per vignette of the reel plan. A camera and frames are measurements; a request is not media."}
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
