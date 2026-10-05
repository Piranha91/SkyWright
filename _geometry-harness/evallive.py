"""evallive — import a live game driver's receipts into an evaluation run (`live-import` checks).

Called by `evalrun.py import-results` when the planned check's adapter kind is `live-import`; it is not an entry
point of its own. The first such check is `runtime.state-switch-flip`, fed by `switchflip.py`.

What it refuses to trust. A live receipt says what the game did, so every claim in it is re-derived here:
  - each launch receipt names the plan, check and arm it was launched for (written by the driver BEFORE the
    launch), and every evidence file is hash-checked;
  - each launch's classification is recomputed from its recorded events by `switchflip_outcome.classify`;
    a stored label that differs is refused, never preferred;
  - the arm's reading is recomputed by `switchflip_outcome.arm_reading`, per condition: an unsettled condition
    (one failure beside one completion, or fewer than two measured launches) cannot be imported;
  - the configuration the driver measured (the profile's plugin files, in order, with their hashes, checked
    against the plugins the running game reported) must equal the arm's frozen request, by evalrun's own
    identity rules.

Import document (schema 1), written by `switchflip.py export`:
  {schemaVersion: 1, planId, checkId, armId, capturedUtc, tool: {name, version?}, state, configuration,
   result: {detector, findings, coverage: [{Population, Judged, Unjudgeable}]},
   outputs: [{role: flip-plan|launch-receipt|events|crash-log, path, sha256}]}
"""
from __future__ import annotations

import json
import os
import shutil
import tempfile
from datetime import datetime
from pathlib import Path
from types import SimpleNamespace

import switchflip_outcome as O

ROLES = ("flip-plan", "launch-receipt", "events", "crash-log")
LAUNCH_SCHEMA = 1


def _parse_iso(s):
    if not isinstance(s, str):
        return None
    try:
        v = datetime.fromisoformat(s.replace("Z", "+00:00"))
    except ValueError:
        return None
    return v if v.tzinfo is not None else None


def read_events(path: Path) -> list[dict]:
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            rows.append(json.loads(line))
    return rows


def reread_launches(launches: list[tuple[dict, list[dict]]]) -> tuple[list[str], dict]:
    """(errors, per-condition readings) for [(launch receipt, its events)]. Pure: the tests drive it directly."""
    errors, by_cond = [], {}
    for rc, events in launches:
        lid = f"{rc.get('condition')}-{rc.get('replicate')}"
        flip_start = rc.get("flipStart")
        window = rc.get("windowSeconds")
        if flip_start is None:
            cls = {"outcome": "not-measured", "branch": None, "why": "the flip was never started"}
        elif not isinstance(window, (int, float)) or window < O.WINDOW_S:
            errors.append(f"launch {lid}: windowSeconds {window!r} is below the rule's {O.WINDOW_S:g} s")
            continue
        else:
            cls = O.classify(events, float(flip_start), float(window))
        stored = rc.get("classification") or {}
        if (stored.get("outcome"), stored.get("branch")) != (cls["outcome"], cls["branch"]):
            errors.append(f"launch {lid}: the stored classification {stored.get('outcome')}/{stored.get('branch')} "
                          f"differs from its events re-read ({cls['outcome']}/{cls['branch']}: {cls['why']}); "
                          "the stored label is never preferred")
            continue
        by_cond.setdefault(str(rc.get("condition")), []).append(cls)
    readings = {c: O.arm_reading(v) for c, v in sorted(by_cond.items())}
    for c, r in readings.items():
        if r["reading"] == "unsettled":
            errors.append(f"condition {c}: unsettled ({r['why']}); an unsettled arm cannot be imported")
    return errors, readings


def derive_result(readings: dict) -> dict:
    findings = sum(1 for r in readings.values() if r["reading"] == "finding")
    coverage = [{"Population": f"switch-flip launches: {c}", "Judged": r["failed"] + r["completed"],
                 "Unjudgeable": dict(r["unjudgeable"])} for c, r in readings.items()]
    return {"detector": "finding" if findings else "no-finding", "findings": findings, "coverage": coverage,
            "readings": readings}


def import_live(E, *, a, runlog, run: Path, src: Path, plan: dict, ledger, check: dict, arm: dict | None,
                doc: dict, manifest_bytes: bytes) -> int:
    """Validate one live import against the frozen plan and write a normal receipt. E is the evalrun module."""
    cid, aid = check["checkId"], doc.get("armId")
    if arm is None or aid not in [x.get("armId") for x in check.get("arms") or []]:
        return E.refuse(f"identity-mismatch: check {cid} has no planned arm {aid!r}")
    row_status = next((x.get("status") for x in check.get("arms") or [] if x.get("armId") == aid), None)
    if row_status != "live-import":
        hint = (" (not opted in: plan again with --opt-in " + cid + ")") if row_status == E.NOT_OPTED_IN else ""
        return E.refuse(f"wrong-check: the plan records {cid} as {row_status!r} on arm {aid}, not live-import, so it "
                        f"is not waiting for a live receipt{hint}")
    key = E.Ledger.key(cid, aid, None)
    if (ledger.entry(cid, aid, None) or {}).get("status") in E.TERMINAL_OK:
        return E.refuse(f"duplicate-receipt: {key} already has accepted evidence")

    errors = []
    captured, tool = doc.get("capturedUtc"), doc.get("tool")
    if _parse_iso(captured) is None:
        errors.append("capturedUtc must be an ISO-8601 timestamp with a timezone")
    if not isinstance(tool, dict) or not tool.get("name"):
        errors.append("the import must name tool.name")

    # ---- evidence files: hash-checked, roles closed
    rows, files = doc.get("outputs"), {r: [] for r in ROLES}
    if not isinstance(rows, list) or not rows:
        errors.append("output-missing: outputs must list the flip plan, each launch receipt and its events")
        rows = []
    for i, row in enumerate(rows):
        if not isinstance(row, dict) or row.get("role") not in ROLES or not row.get("path") \
                or not E.is_sha256(row.get("sha256")):
            errors.append(f"output-missing: outputs[{i}] must name a role ({', '.join(ROLES)}), a path and a "
                          "64-hex sha256")
            continue
        path = Path(row["path"])
        if not path.is_absolute():
            path = src.parent / path
        if not path.is_file():
            errors.append(f"output-missing: {path}")
        elif E.sha256_file(path) != str(row["sha256"]).lower():
            errors.append(f"output-stale: {path} does not hash to the imported sha256")
        elif path.suffix.lower() in E.BINARY_SUFFIXES:
            errors.append(f"output-missing: {path.name} is a binary excluded from bundles")
        else:
            files[row["role"]].append((path, str(row["sha256"]).lower()))
    if len(files["flip-plan"]) != 1:
        errors.append("output-missing: exactly one flip-plan output is required")
    if not files["launch-receipt"]:
        errors.append("output-missing: no launch receipt")
    if errors:
        return E.refuse("import-results refused: " + "; ".join(errors))

    plan_sha = files["flip-plan"][0][1]
    events_by_sha = {h: p for p, h in files["events"]}
    plan_created = _parse_iso(plan.get("createdUtc"))
    launches, fingerprints = [], set()
    for path, _h in files["launch-receipt"]:
        try:
            rc = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as ex:
            errors.append(f"launch receipt {path.name}: not readable as JSON ({ex})")
            continue
        lid = f"{rc.get('condition')}-{rc.get('replicate')}"
        if rc.get("schemaVersion") != LAUNCH_SCHEMA:
            errors.append(f"schema-unsupported: launch {lid} schemaVersion {rc.get('schemaVersion')!r}")
            continue
        bind = rc.get("binding") or {}
        if (bind.get("planId"), bind.get("checkId"), bind.get("armId")) != (plan["planId"], cid, aid):
            errors.append(f"foreign-run: launch {lid} was launched for {bind.get('planId')!r}/{bind.get('checkId')!r}/"
                          f"{bind.get('armId')!r}, not {plan['planId']}/{cid}/{aid}")
        started = _parse_iso(rc.get("launchStartedUtc"))
        if started is None or (plan_created is not None and started < plan_created):
            errors.append(f"foreign-run: launch {lid} started {rc.get('launchStartedUtc')!r}, before this plan was "
                          f"frozen ({plan.get('createdUtc')})")
        if str(rc.get("flipPlanSha256") or "").lower() != plan_sha:
            errors.append(f"identity-mismatch: launch {lid} ran flip plan {str(rc.get('flipPlanSha256'))[:12]}, the "
                          f"import supplies {plan_sha[:12]}")
        fp = ((rc.get("configuration") or {}).get("fingerprint") or "").lower()
        fingerprints.add(fp)
        ev_path = events_by_sha.get(str(rc.get("eventsSha256") or "").lower())
        if ev_path is None:
            errors.append(f"output-missing: launch {lid}'s events file (sha256 {str(rc.get('eventsSha256'))[:12]}) "
                          "is not among the outputs")
            continue
        try:
            launches.append((rc, read_events(ev_path)))
        except (OSError, ValueError) as ex:
            errors.append(f"launch {lid}: events not readable ({ex})")
    cfg_fp = str(((doc.get("configuration") or {}).get("fingerprint")) or "").lower()
    if fingerprints and fingerprints != {cfg_fp}:
        errors.append("identity-mismatch: the launches' configuration fingerprints "
                      f"{sorted(x[:12] for x in fingerprints)} are not the import's {cfg_fp[:12]}")

    re_errors, readings = reread_launches(launches)
    errors.extend(re_errors)
    derived = derive_result(readings) if launches else None
    res = doc.get("result") or {}
    if derived and not re_errors and (res.get("detector"), res.get("findings")) != (derived["detector"],
                                                                                     derived["findings"]):
        errors.append(f"the import states {res.get('detector')}/{res.get('findings')}; its launches re-read as "
                      f"{derived['detector']}/{derived['findings']}")

    # ---- identity: evalrun's own rules, against the arm's frozen request
    limits = []
    identity = E.Validator.emit_identity({"configuration": doc.get("configuration")}) \
        if isinstance(doc.get("configuration"), dict) else None
    if identity is None:
        errors.append("identity-missing: configuration is absent")
    else:
        if doc.get("state") != identity.get("state"):
            errors.append(f"population-mismatch: import state {doc.get('state')!r} differs from configuration state "
                          f"{identity.get('state')!r}")
        for row in identity.get("plugins") or []:
            path, want = Path(str(row.get("path") or "")), row.get("sha256")
            if not row.get("path") or not path.is_file() or not E.is_sha256(want):
                errors.append(f"identity-missing: plugin {row.get('name')} must name an existing path and 64-hex sha256")
            elif E.sha256_file(path) != str(want).lower():
                errors.append(f"identity-mismatch: plugin {row.get('name')} does not hash to its stated sha256")
        validator = E.Validator(run, plan)
        errors.extend(f"{x['cause']}: {x['detail']}" for x in
                      validator._identity_checks(identity, arm, doc, check.get("adapter") or {}, None, limits, None))
        req = arm.get("requested") or {}
        expected_fp = E.configuration_fingerprint(
            req.get("plugins") or [], req.get("providers") or [], req.get("overlays") or [],
            req.get("population"), identity.get("state"), schema=identity.get("schemaVersion"))
        if identity.get("fingerprint") != expected_fp:
            errors.append(f"identity-mismatch: imported fingerprint {str(identity.get('fingerprint'))[:12]} does not "
                          f"equal this arm's frozen request {expected_fp[:12]}")
    for lim in (doc.get("configuration") or {}).get("limits") or []:
        limits.append({"detail": str(lim)})

    gate = SimpleNamespace(ledger=ledger)
    pre = E.prereq_satisfied(gate, check, aid)
    if pre:
        errors.append(f"blocked-prerequisite: {E.prereq_block_detail(gate, pre, aid)}. Settle {pre} on this arm, "
                      "then import again")
    if errors:
        return E.refuse("import-results refused: " + "; ".join(errors))

    # ---- copy the evidence beside the run, then write the receipt
    sources = [p for r in ROLES for p, _h in files[r]]
    dest, n = run / "imports" / key, 1
    while dest.exists():
        n += 1
        dest = run / "imports" / f"{key}~{n}"
    dest.parent.mkdir(parents=True, exist_ok=True)
    temp = Path(tempfile.mkdtemp(prefix=key + "-", dir=dest.parent))
    try:
        (temp / "result.json").write_bytes(manifest_bytes)
        names = [f"{i:02d}-{p.name}" for i, p in enumerate(sources, start=1)]
        for p, name in zip(sources, names):
            shutil.copy2(p, temp / name)
        if [E.sha256_file(temp / nm) for nm in names] != [E.sha256_file(p) for p in sources]:
            return E.refuse("output-stale: imported evidence changed while it was being copied")
        os.replace(temp, dest)
    finally:
        if temp.exists():
            shutil.rmtree(temp, ignore_errors=True)
    copied = [dest / "result.json"] + [dest / nm for nm in names]
    runlog.feature("outputs", len(copied), "files")
    receipt = E.write_receipt(
        run, plan=plan, check=check, arm=arm, part=None,
        adapter={"kind": "live-import", "sourceKind": "live-import"}, argv=None,
        run_id=runlog.run_id, parent_run_id=None, started=captured, ended=E.utcnow(), exit_code=0,
        outputs=copied, log=None,
        consumed={"candidateSha256": arm.get("candidateSha256"), "candidatePath": arm.get("candidatePath"),
                  "configuration": identity, "configurationSource": "live-driver",
                  "registrySha256": plan["registry"]["sha256"], "tool": tool,
                  "optionsSha256": E.sha256_text(E.canonical(plan["options"])),
                  "scopeSha256": plan["scope"]["sha256"]},
        validation={"accepted": True, "rejections": [], "identityLimits": limits},
        result={"execution": "completed", "detector": derived["detector"], "findings": derived["findings"],
                "coverage": derived["coverage"], "strength": (check.get("evidence") or {}).get("strength"),
                "verdict": None, "readings": derived["readings"],
                "note": "re-read from the launches' events by switchflip_outcome; a completion is no failure under "
                        "the listed conditions only"},
        extra={"imported": {"sourceSha256": E.sha256_file(copied[0]), "capturedUtc": captured, "tool": tool,
                            "origin": "fresh", "bindingRecordedAtLaunch": True, "flipPlanSha256": plan_sha,
                            "launches": len(launches)}})
    ledger.transition(cid, aid, None, "completed", receipt_id=receipt["receiptId"], detector=derived["detector"],
                      extra={"imported": True, "importOrigin": "fresh"})
    runlog.artifact(str(copied[0]))
    runlog.advance()
    print(f"imported {cid} [{aid}]: {derived['detector']}, {derived['findings']} condition(s) with a finding, "
          f"{len(launches)} launch(es) re-read")
    return E.EXIT_OK
