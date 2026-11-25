"""Build the evalreport fixtures: schema-2 bundles for cases R1-R7, made by driving the REAL controller (the built-in
one, runcontrol: plan / run / finalize / bundle, started as test_evalrun's CONTROLLER) over
`test_evalrun.py`'s fixture engine - the tiny plugins, the trimmed registry, the fake probes that write the real
conflict-file format, the real conflictdiff.exe.

    python _geometry-harness/tests/fixtures/evalreport/make_report_fixtures.py --out DIR

Nothing it writes is tracked: every bundle carries paths and hashes of files this run wrote. `--out` defaults
to the gitignored `_generated/` beside this script; the suite passes its own temp directory. The result is
`DIR/index.json`: `{ "<case>": { "bundle": "<dir>", ... } }`.

Cases (EXPECTED.md section 1):
  R1 intact-incomplete            case-15's shape (case-01's scenario)
  R2 null-vs-missing              case-03
  R3 many-findings-long-names     a new case: 62 rows per arm in four (Kind, A, B) groups, 80+ char names
  R4 repaired-plus-regression     case-07
  R5 media-mismatch-same-basenames case-10's run + a D5 schema-2 media.json (five items), written for the caller's
                                  ai_hook to bundle (supplied media is an AI-side overlay's: the houseCARL integration
                                  module's suite passes the hook; without one R5 has no media bundle)
  R6 changed-or-missing-source    two damaged COPIES: of R5's bundles (when the hook made them) and of R1's bundle
  R7 import-origin                made by the caller's ai_hook only (imported evidence is an AI-side overlay's: the
                                  houseCARL integration module's suite passes the hook; without one there is no R7)

Builds conflictdiff from this checkout's source into the output folder (test_evalrun.use_conflictdiff), unless
HOUSECARL_EVALRUN_TEST_HARNESS names a tree to use as given.
"""
from __future__ import annotations

import argparse
import copy
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
TESTS = HERE.parents[1]                       # _geometry-harness/tests
EVALRUN_FIXT = TESTS / "fixtures" / "evalrun"
sys.path.insert(0, str(TESTS))
sys.path.insert(0, str(EVALRUN_FIXT))

import test_evalrun as T                      # noqa: E402  (the fixture engine; main() is guarded)

CASES = {
    "R1-intact-incomplete": "case-15-portable-incomplete",
    "R2-null-vs-missing": "case-03-null-and-extraction",
    "R3-many-findings-long-names": "R3-many-findings-long-names",
    "R4-repaired-plus-regression": "case-07-combined-regression",
    "R5-media-mismatch-same-basenames": "case-10-discovery-media",
}

LONG = {
    "A": "FixtureLongNameAlpha-the-first-plugin-whose-name-runs-past-eighty-characters-for-the-print-check.esp",
    "B": "FixtureLongNameBravo-the-second-plugin-whose-name-runs-past-eighty-characters-for-the-print-check.esp",
    "C": "FixtureLongNameCharlie-the-third-plugin-whose-name-runs-past-eighty-characters-for-the-print-check.esp",
    "D": "FixtureLongNameDelta-the-fourth-plugin-whose-name-runs-past-eighty-characters-for-the-print-check.esp",
}
OTHER_PROBES = ("bosprobe", "switchprobe", "stateprobe", "scopeprobe", "adviceprobe", "patchprobe", "driftprobe")


def write_json(p: Path, doc):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")


def load(p: Path):
    return json.loads(Path(p).read_text(encoding="utf-8"))


def sha256_file(p: Path) -> str:
    import hashlib
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


# ---------------------------------------------------------------------------------- R3's rows

def long_rows(kind: str, a: str, b: str, base_hex: int, n: int, role: str, label: str, summary: str) -> list:
    rows = []
    for i in range(n):
        rows.append({"kind": kind, "a": a, "b": b, "summary": summary,
                     "formKey": f"{base_hex + i:06X}:FixtureBase.esp", "role": role, "label": label,
                     "physical": "Holds", "detail": f"fixture row {i + 1} of {n} in this group"})
    return rows


def r3_rows_by_probe() -> dict:
    """62 rows per arm: navownprobe 30 (two groups), overlapprobe 20 (one), contactprobe 12 (one)."""
    for name in LONG.values():
        assert len(name) >= 80, name
    nav = (long_rows("CLOBBERED", LONG["A"], LONG["B"], 0x0B0001, 20, "navmesh", "FixtureInterior01",
                     f"20 navmesh record(s) re-cut by {LONG['A']} and then re-cut again by {LONG['B']}, which declares no dependency on it")
           + long_rows("CLOBBERED", LONG["C"], LONG["D"], 0x0B0101, 10, "navmesh", "FixtureExterior",
                       f"10 navmesh record(s) re-cut by {LONG['C']} and then re-cut again by {LONG['D']}, which declares no dependency on it"))
    for i, row in enumerate(nav):
        row["physical"] = "Gone" if i == 0 else "Holds"
        row["detail"] = (f"no point of the loser's mesh is farther from the winner's ({row['b']})" if i == 0 else
                         f"victim-only ground on the loser's mesh farther from the winner's ({row['b']})")
    ovl = long_rows("OVERLAP", LONG["A"], LONG["B"], 0x0B0201, 20, "reference", "FixtureInterior01",
                    "two placed references intersect")
    con = long_rows("CONTACT", LONG["C"], LONG["B"], 0x0B0301, 12, "reference", "FixtureExterior",
                    "a dependent reference touches nothing")
    return {"navownprobe": nav, "overlapprobe": ovl, "contactprobe": con}


def make_r3_case(gen_root: Path):
    """case-01's scope, candidates and registry; a scenario whose fake probes emit the long-named rows on the
    baseline arm, the candidate arm and its reverse part (identical rows: the sweep is INERT)."""
    src = gen_root / "cases" / "case-01-geometry-and-quest"
    dst = gen_root / "cases" / "R3-many-findings-long-names"
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)
    scen = load(dst / "scenario.json")
    scen["scopeFrom"] = str(dst / "scope.json").replace("\\", "/")
    stems = ["baseline", "candidate-01-reorder", "candidate-01-reorder.reverse"]
    arms = {}
    for probe, rows in r3_rows_by_probe().items():
        arms[probe] = {s: {"behaviour": "ok", "rows": rows, "judged": 420} for s in stems}
    for probe in OTHER_PROBES:
        arms[probe] = {"*": {"behaviour": "ok", "rows": [], "judged": 42}}
    scen["arms"] = arms
    write_json(dst / "scenario.json", scen)
    return dst


def make_r5_case(gen_root: Path):
    """Keep R5's strict media controls attached to independently reportable evidence.

    The source evalrun fixture uses a CLOBBERED ownership row.  That row is now reader context only,
    so it cannot be the positive control for illustrated report chapters.  R5 keeps the same record,
    arms, receipts, fingerprints and media identities while making the measured condition an active
    broken edge-link finding.
    """
    case = gen_root / "cases" / "case-10-discovery-media"
    scen = load(case / "scenario.json")
    for arm in (scen.get("arms") or {}).get("navownprobe", {}).values():
        for row in arm.get("rows") or []:
            row.update({
                "kind": "EDGE-IDX",
                "summary": "a navmesh link points to a nonexistent edge",
                "physical": "Holds",
                "detail": "the linked triangle index exceeds the target navmesh triangle count",
            })
    write_json(case / "scenario.json", scen)
    return case


# ---------------------------------------------------------------------------------- R5's media

def png_bytes(colour: tuple) -> bytes:
    """A 4x4 PNG. PIL when present (the plan measured it present); a hand-built one otherwise, so the
    generator has no hard dependency on an image library for what is only a hashed file."""
    try:
        from PIL import Image
        import io
        im = Image.new("RGB", (4, 4), colour)
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        return buf.getvalue()
    except Exception:
        import struct
        import zlib
        raw = b"".join(b"\x00" + bytes(colour) * 4 for _ in range(4))

        def chunk(tag, data):
            return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)
        return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 4, 4, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def accepted_fingerprint(run: Path, arm: str) -> str:
    """The configuration fingerprint an ACCEPTED navownprobe receipt on `arm` consumed."""
    rc = load(run / "receipts" / f"geometry.navmesh-ownership__{arm}__arm.json")
    assert (rc.get("validation") or {}).get("accepted") is True, f"receipt for {arm} is not accepted"
    return rc["consumed"]["configuration"]["fingerprint"]


def write_media(run: Path, root: Path) -> Path:
    """D5's schema-2 media.json: (a) and (b) accepted with the SAME basename on two arms, (c) fingerprint
    mismatch, (d) no mediaKind, (e) a clip whose source is absent."""
    fp_base = accepted_fingerprint(run, "baseline")
    fp_cand = accepted_fingerprint(run, "candidate-01-reorder")
    m = root / "supplied-media"
    (m / "renders-baseline").mkdir(parents=True, exist_ok=True)
    (m / "renders-candidate").mkdir(parents=True, exist_ok=True)
    a = m / "renders-baseline" / "cell-view.png"
    b = m / "renders-candidate" / "cell-view.png"
    c = m / "stale-render.png"
    d = m / "unkinded.png"
    e = m / "walk.mp4"
    f = m / "no-provenance.png"
    a.write_bytes(png_bytes((200, 30, 30)))
    b.write_bytes(png_bytes((30, 200, 30)))
    c.write_bytes(png_bytes((30, 30, 200)))
    d.write_bytes(png_bytes((120, 120, 120)))
    e.write_bytes(b"\x00\x00\x00\x18ftypmp42" + b"fixture clip stub: the bundle side only hashes this file\n")
    f.write_bytes(png_bytes((200, 200, 30)))
    absent_source = str(m / "absent" / "session.mp4").replace("\\", "/")
    prov = {"tool": "viewprobe", "toolVersion": "fixture", "capturedUtc": "2026-09-12T18:00:00Z",
            "note": "fixture render: a 4x4 image standing in for a viewprobe frame"}

    def item(path, arm, fp, kind="offline-render", **extra):
        it = {"path": str(path).replace("\\", "/"), "mediaKind": kind,
              "subject": {"checkId": "geometry.navmesh-ownership", "armId": arm,
                          "formKey": "0A0001:FixtureBase.esp", "description": "the re-cut navmesh's cell"},
              "state": "baseline", "configurationFingerprint": fp, "fingerprintSource": "asserted",
              "provenance": dict(prov)}
        it.update(extra)
        return it

    # (c) is A-3's trap (PLAN.md amendment, Fable 18:58): declared on the CANDIDATE arm but carrying the
    # BASELINE order's fingerprint - which that arm's `reverse` receipt also consumed. Only the arm's own
    # receipts (part null / arm) count, so this is fingerprint-mismatch, never an illustration of the candidate.
    items = [
        item(a, "baseline", fp_base),
        item(b, "candidate-01-reorder", fp_cand),
        item(c, "candidate-01-reorder", fp_base),
        item(d, "baseline", fp_base),
        item(e, "baseline", fp_base, kind="game-video",
             provenance={"tool": "obs", "capturedUtc": "2026-09-12T18:05:00Z", "runId": "fixture-run"},
             sourceInterval={"startSeconds": 2.0, "endSeconds": 5.5, "timebase": "seconds",
                             "sourcePath": absent_source, "sourceSha256": "0" * 64}),
    ]
    del items[3]["mediaKind"]                                          # (d): kind-missing
    # (f): otherwise valid, no provenance at all -> provenance-missing (D5 as extended 20:42; checked after
    # fingerprint-source-missing). Every other item carries provenance {tool, capturedUtc}.
    items.append(item(f, "baseline", fp_base))
    del items[5]["provenance"]
    p = root / "media.json"
    write_json(p, items)
    return p


# ---------------------------------------------------------------------------------- the build

def run_case(root: Path, case: str, fixture_case: str, media: Path | None = None) -> dict:
    run = root / "runs" / case
    _, _, _, scen = T.case_paths(fixture_case)
    p = T.do_plan(run, fixture_case)
    if p.returncode != 0:
        raise SystemExit(f"{case}: the controller's plan exited {p.returncode}\n{p.stdout[-2000:]}\n{p.stderr[-2000:]}")
    r = T.evalrun(["run", "--run", run], scen)
    f = T.evalrun(["finalize", "--run", run], scen)
    if media is None and case.startswith("R5"):
        media = write_media(run, root / "media" / case)
    # the report fixtures' expectations are written without names: the names step is skipped (stated in the manifest);
    # the media file is the caller's hook's to bundle (generate's ai_hook)
    bargs = ["bundle", "--run", run, "--no-names"]
    b = T.evalrun(bargs, scen)
    if b.returncode != 0:
        raise SystemExit(f"{case}: the controller's bundle exited {b.returncode}\n{b.stdout[-2000:]}\n{b.stderr[-2000:]}")
    return {"run": str(run), "bundle": str(run / "bundle"), "fixtureCase": fixture_case,
            "exits": {"plan": p.returncode, "run": r.returncode, "finalize": f.returncode, "bundle": b.returncode},
            "media": str(media) if media else None}


def make_r6(root: Path, index: dict) -> dict:
    """Two damaged copies. (a) an accepted media file's bytes altered after bundling (R5's app bundle and the overlay
    bundle bound to it, copied side by side; only when the caller's hook bundled R5's media); (b) a schema-1 manifest."""
    out = {}
    r5 = index.get("R5-media-mismatch-same-basenames") or {}
    if r5.get("aiBundle"):
        a = root / "bundles" / "R6-media-bytes-altered"
        if a.exists():
            shutil.rmtree(a)
        shutil.copytree(r5["bundle"], a / "bundle")
        shutil.copytree(r5["aiBundle"], a / Path(r5["aiBundle"]).name)
        for f in (a / "bundle" / "verify.json", a / Path(r5["aiBundle"]).name / "verify.json"):
            f.unlink(missing_ok=True)
        media = a / Path(r5["aiBundle"]).name / "media"
        media_files = sorted(p for p in media.glob("*") if p.is_file()) if media.exists() else []
        if not media_files:
            raise SystemExit("R6: R5's overlay bundle copied no media file, so there is nothing to alter "
                             "(bundle --media accepted nothing - see its manifest's unassessedMedia)")
        victim = media_files[0]
        victim.write_bytes(victim.read_bytes() + b"\nbytes altered after bundling\n")
        out["media-bytes-altered"] = {"bundle": str(a / "bundle"), "aiBundle": str(a / Path(r5["aiBundle"]).name),
                                      "altered": victim.name}

    src1 = Path(index["R1-intact-incomplete"]["bundle"])
    b = root / "bundles" / "R6-manifest-schema-1"
    if b.exists():
        shutil.rmtree(b)
    shutil.copytree(src1, b)
    (b / "verify.json").unlink(missing_ok=True)
    man = load(b / "manifest.json")
    man["bundleSchema"] = 1
    write_json(b / "manifest.json", man)
    out["manifest-schema-1"] = {"bundle": str(b)}
    return out


def generate(root: Path, only: str | None = None, ai_hook=None) -> dict:
    """ai_hook(root, index, only): the caller's step that adds what an AI-side evaluation adds (R5's media, R7's
    imports), run after the app bundles; it records each overlay bundle in its case as aiBundle."""
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    gen = root / "evalrun-fixtures"
    r = subprocess.run([sys.executable, str(EVALRUN_FIXT / "make_fixtures.py"), "--out", str(gen)],
                       capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit("make_fixtures.py did not run:\n" + r.stdout + r.stderr)
    make_r3_case(gen)
    make_r5_case(gen)
    T.GEN = gen
    # EVALRUN_CONTROLLER_CMD (test_evalrun's) names another build of the controller to make the bundles with
    os.environ.setdefault("HOUSECARL_RUNLOG_DIR", str(root / "runlog"))
    os.environ["PYTHONIOENCODING"] = "utf-8"
    # These bundles are load-time fixtures: plan them at Standard depth explicitly, whatever the evaluation's default
    # depth or the user's saved settings say.
    T.pin_standard_settings(root)
    T.use_conflictdiff(root)                  # a no-op when the calling suite already chose this run's conflictdiff
    if not T.DIFF_EXE.exists():
        raise SystemExit(f"conflictdiff is not built at {T.DIFF_EXE} - build it or set "
                         f"HOUSECARL_EVALRUN_TEST_HARNESS to a tree that has it.")
    index = {}
    for case, fixture_case in CASES.items():
        if only and not case.startswith(only) and not only.startswith(case) and not (
                only.startswith("R6") and case.startswith(("R1", "R5"))):
            continue
        index[case] = run_case(root, case, fixture_case)
        print(f"  {case}: bundle at {index[case]['bundle']}")
    if ai_hook is not None:
        ai_hook(root, index, only)
    if not only or only.startswith("R6"):
        index["R6-changed-or-missing-source"] = make_r6(root, index)
        print("  R6-changed-or-missing-source: damaged copies written")
    index["_fixtureRoot"] = str(gen)
    write_json(root / "index.json", index)
    return index


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(HERE / "_generated"))
    ap.add_argument("--only", help="one case by prefix (R6 also builds R1 and R5)")
    a = ap.parse_args()
    generate(Path(a.out), a.only)
    print(f"fixtures written under {a.out}")
    print(T.DIFF_SOURCE)
    print(T.diff_exe_identity())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
