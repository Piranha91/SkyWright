"""A run keeps each reader transcript once: imports and bundles link it (cases 2160-2165).

    python _geometry-harness/tests/test_transcript_links.py

`evalrun import-results` copied an arm's request and response transcripts into the run once per check, and
`evalrun bundle` copied them again into the bundle and the media bundle: about 134 KB per evaluated record, so
Hroggar's run held the same 6 GB of transcripts four times over and the Legacy subjects could not fit on disk. Now the
import hard-links the capture's own transcript when the volume allows, and the bundle links the import's files; a
writer that later rewrites a transcript writes a new file and moves it over, so bytes reachable through a link never
change.

Drives the REAL evalrun.py over the evalrun fixtures (the same set-up as test_import_empty_population.py), with
transcripts laid out as evalrecords lays them out (`sessions/<arm>/request.jsonl`, `response.jsonl`, and a rows file).

    2160  an accepted import links its request and response to the capture's files, with the recorded hashes; the rows
          file is a copy.
    2161  where a link cannot be made (another volume), the import copies and is still accepted with the same hashes.
    2162  a transcript changed after its import document was written is refused, and nothing is linked to it.
    2163  the bundle, and a second bundle such as the media bundle, link the import's transcripts; control files are
          copies.
    2164  verify-bundle passes on both linked bundles.
    2165  a rerun that rewrites the capture's transcripts (evalrecords' streaming writer and its in-memory writer) and
          its rows file, then a resume, changes no byte reachable through a link: every imported and bundled file
          still hashes as recorded, and both bundles still verify.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
FIXT = HERE / "fixtures" / "evalrun"
REGISTRY = REPO / ".claude" / "skills" / "world-edit-compatibility" / "references" / "check-registry.json"
DIALOGUE = "records.registration-and-dialogue"
ASSETS = "loadability.missing-assets"
KEEP = ["loadability.masters-and-order", DIALOGUE, ASSETS]
REORDER = "candidate-01-reorder"
# runs evalrun as a script with os.link failing as it does across volumes
NO_LINK = ("import errno, os, runpy, sys\n"
           "def nolink(*a, **k):\n"
           "    raise OSError(errno.EXDEV, 'not on one volume')\n"
           "os.link = nolink\n"
           "sys.argv = sys.argv[1:]\n"
           "runpy.run_path(sys.argv[0], run_name='__main__')\n")

sys.path.insert(0, str(HARNESS))
import evalrecords  # noqa: E402

FAILURES: list[str] = []


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def evalrun(args, env, no_link=False):
    pre = [sys.executable, "-c", NO_LINK, str(EVALRUN)] if no_link else [sys.executable, str(EVALRUN)]
    return subprocess.run(pre + [str(a) for a in args], capture_output=True, text=True, encoding="utf-8",
                          errors="replace", env=env, timeout=300)


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def same(a: Path, b: Path) -> bool:
    return a.is_file() and b.is_file() and os.path.samefile(a, b)


def fingerprint(cfg: dict) -> str:
    lines = [f"schema:{cfg.get('schemaVersion', 1)}\n", f"population:{cfg.get('population')}\n",
             f"state:{cfg.get('state')}\n"]
    lines += [f"plugin:{str(p.get('name') or '').lower()}:{p.get('sha256') or ''}\n" for p in cfg.get("plugins") or []]
    lines += [f"provider:{p.get('kind') or ''}:{str(p.get('name') or '').lower()}\n" for p in cfg.get("providers") or []]
    lines += [f"overlay:{o.get('kind') or ''}:{o.get('sha256') or ''}\n" for o in cfg.get("overlays") or []]
    return hashlib.sha256("".join(lines).encode("utf-8")).hexdigest()


def capture(home: Path, plan: dict, arm: dict) -> Path:
    """An arm's session as evalrecords leaves it: sessions/<arm>/request.jsonl and response.jsonl."""
    sdir = home / "sessions" / arm["armId"]
    sdir.mkdir(parents=True, exist_ok=True)
    init = {"jsonrpc": "2.0", "id": 0, "method": "initialize",
            "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                       "clientInfo": {"name": f"evalrun:{plan['planId']}:{arm['armId']}", "version": "fixture-capture 1"}}}
    (sdir / "request.jsonl").write_text(json.dumps(init) + "\n" + json.dumps(
        {"tool": "fixture_mcp", "arguments": {"plugins": arm["plugins"]}}) + "\n", encoding="utf-8")
    (sdir / "response.jsonl").write_text(json.dumps({"findings": 0, "pad": "x" * 4096}) + "\n", encoding="utf-8")
    return sdir


def make_import(home: Path, sdir: Path, plan: dict, cid: str, arm: dict) -> Path:
    """evalrecords' write_imports shape: imports/<check>__<arm>/rows.json and import.json naming the session files."""
    folder = home / "imports" / f"{cid}__{arm['armId']}"
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "rows.json").write_text(json.dumps({"Population": cid, "State": "baseline", "Coverage": [],
                                                  "Conflicts": []}, indent=1), encoding="utf-8")
    req = arm["requested"]
    cfg = {"schemaVersion": 1, "source": f"candidate:{arm['candidateFile']}",
           "plugins": [{"name": p["name"], "path": p["path"], "sha256": p["sha256"]} for p in req["plugins"]],
           "providers": [dict(v) for v in req["providers"]], "overlays": req["overlays"],
           "population": req["population"], "state": "baseline"}
    cfg["fingerprint"] = fingerprint(cfg)
    doc = {"schemaVersion": 1, "planId": plan["planId"], "checkId": cid, "armId": arm["armId"], "state": "baseline",
           "origin": "fresh", "capturedUtc": datetime.now(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z"),
           "tool": {"name": "fixture_mcp", "version": "1"}, "configuration": cfg,
           "result": {"detector": "no-finding", "findings": 0,
                      "coverage": [{"Population": "records", "Judged": 1, "Unjudgeable": {}}]},
           "outputs": [{"role": "request", "path": str(sdir / "request.jsonl"), "sha256": sha(sdir / "request.jsonl")},
                       {"role": "response", "path": str(sdir / "response.jsonl"), "sha256": sha(sdir / "response.jsonl")},
                       {"role": "rows", "path": "rows.json", "sha256": sha(folder / "rows.json")}]}
    (folder / "import.json").write_text(json.dumps(doc, indent=1), encoding="utf-8")
    return folder / "import.json"


def setup(tmp: Path) -> dict:
    gen = tmp / "fx"
    subprocess.run([sys.executable, str(FIXT / "make_fixtures.py"), "--out", str(gen)], check=True,
                   capture_output=True, text=True)
    real = json.loads(REGISTRY.read_text(encoding="utf-8"))
    kept = [c for c in real["checks"] if c["id"] in KEEP]
    reg = dict(real, checks=kept, families=[f for f in real["families"] if f["id"] in {c["family"] for c in kept}])
    (tmp / "registry.json").write_text(json.dumps(reg, indent=1), encoding="utf-8")
    case = gen / "cases" / "case-01-geometry-and-quest"
    cands = tmp / "candidates"
    cands.mkdir()
    for f in ("candidate-00-baseline.json", "candidate-01-reorder.json"):
        shutil.copy2(case / "candidates" / f, cands / f)
    return {"registry": tmp / "registry.json", "scope": case / "scope.json", "cands": cands,
            "scenario": case / "scenario.json"}


def receipt(run: Path, cid: str, aid: str) -> dict:
    p = run / "receipts" / f"{cid}__{aid}.json"
    return json.loads(p.read_text(encoding="utf-8")) if p.is_file() else {}


def recorded_ok(run: Path, bundles: list[Path]) -> list[str]:
    """Every imported file against its receipt's hash, every bundled file against its manifest row's."""
    bad = []
    for r in (run / "receipts").glob("*.json"):
        for o in json.loads(r.read_text(encoding="utf-8")).get("outputs") or []:
            p = run / o["path"]
            if str(o["path"]).startswith("imports/") and (not p.is_file() or sha(p) != o["sha256"]):
                bad.append(str(p))
    for b in bundles:
        if not (b / "manifest.json").is_file():
            bad.append(str(b / "manifest.json"))
            continue
        for row in json.loads((b / "manifest.json").read_text(encoding="utf-8")).get("evidence") or []:
            p = b / row["path"]
            if not p.is_file() or sha(p) != row["sha256"]:
                bad.append(str(p))
    return bad


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-transcript-links-"))
    try:
        fx = setup(tmp)
        env = dict(os.environ, EVALRUN_FAKE_SCENARIO=str(fx["scenario"]), HOUSECARL_RUNLOG_DIR=str(tmp / "runlog"),
                   PYTHONIOENCODING="utf-8")
        for k in ("HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH"):
            env.pop(k, None)
        run = tmp / "run"
        p = evalrun(["plan", "--registry", fx["registry"], "--scope", fx["scope"], "--candidates", fx["cands"],
                     "--out", run, "--harness", HARNESS, "--depth", "standard",
                     "--probe-cmd", f"scopeprobe={sys.executable} {FIXT / 'fakeprobes' / 'scopeprobe'}.py"], env)
        plan = json.loads((run / "plan.json").read_text(encoding="utf-8")) if (run / "plan.json").exists() else {}
        arms = {a["armId"]: a for a in plan.get("arms") or []}
        r = evalrun(["run", "--run", run], env)
        check(p.returncode == 0 and r.returncode == 0 and "baseline" in arms and REORDER in arms,
              f"setup: plan {p.returncode}, run {r.returncode}, arms {sorted(arms)}")
        if "baseline" not in arms or REORDER not in arms:
            return 1
        home = tmp / "records" / "checkpoints"
        s_base = capture(home / "baseline", plan, arms["baseline"])
        s_re = capture(home / REORDER, plan, arms[REORDER])

        print("\n=== case-2160: an accepted import links its transcripts and copies its rows ===")
        m = make_import(home / "baseline", s_base, plan, DIALOGUE, arms["baseline"])
        r = evalrun(["import-results", "--run", run, "--file", m], env)
        imp = run / "imports" / f"{DIALOGUE}__baseline"
        outs = {Path(o["path"]).name: o["sha256"] for o in receipt(run, DIALOGUE, "baseline").get("outputs") or []}
        check(r.returncode == 0 and same(imp / "01-request.jsonl", s_base / "request.jsonl")
              and same(imp / "02-response.jsonl", s_base / "response.jsonl")
              and outs.get("02-response.jsonl") == sha(s_base / "response.jsonl")
              and (imp / "03-rows.json").is_file()
              and not same(imp / "03-rows.json", m.parent / "rows.json"),
              f"C2160: request and response linked with the recorded hashes, rows copied (exit {r.returncode}; "
              f"{r.stderr.strip()[-300:]!r})")

        print("\n=== case-2161: no link possible (another volume): copied, still accepted ===")
        m = make_import(home / "baseline", s_base, plan, ASSETS, arms["baseline"])
        r = evalrun(["import-results", "--run", run, "--file", m], env, no_link=True)
        imp = run / "imports" / f"{ASSETS}__baseline"
        outs = {Path(o["path"]).name: o["sha256"] for o in receipt(run, ASSETS, "baseline").get("outputs") or []}
        check(r.returncode == 0 and (imp / "02-response.jsonl").is_file()
              and not same(imp / "02-response.jsonl", s_base / "response.jsonl")
              and outs.get("02-response.jsonl") == sha(imp / "02-response.jsonl") == sha(s_base / "response.jsonl"),
              f"C2161: copied with the recorded hash and accepted (exit {r.returncode}; {r.stderr.strip()[-300:]!r})")

        print("\n=== case-2162: a transcript changed after its import document: refused, nothing linked ===")
        m = make_import(home / REORDER, s_re, plan, DIALOGUE, arms[REORDER])
        with open(s_re / "response.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps({"late": True}) + "\n")
        r = evalrun(["import-results", "--run", run, "--file", m], env)
        check(r.returncode != 0 and "output-stale" in r.stderr
              and not any((run / "imports").glob(f"{DIALOGUE}__{REORDER}*"))
              and os.stat(s_re / "response.jsonl").st_nlink == 1,
              f"C2162: refused as stale, no import folder, the transcript not linked (exit {r.returncode}; "
              f"{r.stderr.strip()[-300:]!r})")
        capture(home / REORDER, plan, arms[REORDER])
        m = make_import(home / REORDER, s_re, plan, DIALOGUE, arms[REORDER])
        r = evalrun(["import-results", "--run", run, "--file", m], env)
        check(r.returncode == 0, f"C2162: the re-captured import is then accepted (exit {r.returncode}; "
                                 f"{r.stderr.strip()[-300:]!r})")

        print("\n=== case-2163: the bundle and a second (media) bundle link the import's transcripts ===")
        evalrun(["finalize", "--run", run], env)
        b1, b2 = run / "bundle", tmp / "media-bundle" / "evidence"
        r1 = evalrun(["bundle", "--run", run], env)
        r2 = evalrun(["bundle", "--run", run, "--out", b2], env)
        resp = f"evidence/imports/{DIALOGUE}__baseline/02-response.jsonl"
        check(r1.returncode == 0 and r2.returncode == 0
              and same(b1 / resp, s_base / "response.jsonl") and same(b2 / resp, s_base / "response.jsonl")
              and (b1 / "ledger.json").is_file() and os.stat(b1 / "ledger.json").st_nlink == 1,
              f"C2163: both bundles link the transcript, control files are copies (exit {r1.returncode}/"
              f"{r2.returncode}; {(r1.stderr + r2.stderr).strip()[-300:]!r})")

        print("\n=== case-2164: verify-bundle passes on both linked bundles ===")
        v1, v2 = evalrun(["verify-bundle", b1], env), evalrun(["verify-bundle", b2], env)
        check(v1.returncode == 0 and v2.returncode == 0 and same(b1 / resp, s_base / "response.jsonl"),
              f"C2164: both verify while linked (exit {v1.returncode}/{v2.returncode}; "
              f"{(v1.stdout + v1.stderr + v2.stderr).strip()[-300:]!r})")

        print("\n=== case-2165: a rerun and a resume change no byte reachable through a link ===")
        before = {p: sha(p) for p in (b1 / resp, b2 / resp, run / "imports" / f"{DIALOGUE}__baseline" / "02-response.jsonl")
                  if p.is_file()}
        resp_re = f"evidence/imports/{DIALOGUE}__{REORDER}/02-response.jsonl"
        before.update({p: sha(p) for p in (b1 / resp_re, b2 / resp_re) if p.is_file()})
        linked = all(same(b / r_, s / "response.jsonl") for b in (b1, b2) for r_, s in ((resp, s_base), (resp_re, s_re)))
        # the in-memory writer
        s = evalrecords.Session.__new__(evalrecords.Session)
        s.req, s.resp = [{"rerun": 1}], [{"rerun": 1, "answer": "different"}]
        s.write(s_base)
        # the streaming writer a records rerun uses, on the other arm's still-linked transcripts
        t_req = evalrecords.TranscriptFile(s_re / "request.jsonl")
        t_resp = evalrecords.TranscriptFile(s_re / "response.jsonl")
        t_req.append({"rerun": 2})
        t_resp.append({"rerun": 2, "answer": "different again"})
        t_req.finish()
        t_resp.finish()
        # write_imports rewrites its rows file in place
        (home / "baseline" / "imports" / f"{DIALOGUE}__baseline" / "rows.json").write_text(
            json.dumps({"Conflicts": [{"A": "x", "B": "y"}]}), encoding="utf-8")
        rs = evalrun(["resume", "--run", run], env)
        v1, v2 = evalrun(["verify-bundle", b1], env), evalrun(["verify-bundle", b2], env)
        bad = recorded_ok(run, [b1, b2])
        changed = [str(p) for p, h in before.items() if sha(p) != h]
        check(linked and len(before) == 5 and rs.returncode == 0 and not bad and not changed
              and v1.returncode == 0 and v2.returncode == 0
              and sha(s_base / "response.jsonl") not in before.values() and sha(s_re / "response.jsonl") not in before.values(),
              f"C2165: imported and bundled bytes unchanged after the rewrite and resume; both bundles verify "
              f"(linked before {linked}, resume {rs.returncode}, verify {v1.returncode}/{v2.returncode}; changed {changed}; not as recorded "
              f"{bad[:3]})")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
