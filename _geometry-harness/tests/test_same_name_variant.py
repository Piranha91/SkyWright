"""A same-named plugin from another folder is planned and reported as itself (cases 451-459).

    python _geometry-harness/tests/test_same_name_variant.py

Authors ship "compatible" versions of their own plugin under the same file name. An arm whose mod list puts that
folder first loads the variant, so the plan must request the variant's bytes (SW1: it used to request the copy the
scope measured, and the run then rejected every emit of the arm), and the report must say the arm replaced a plugin
(SW2: it compared names only and called the arm a same-order control). Plan cases drive the REAL evalrun.py (and the
real overlapprobe for the plan-time preflight) on a temporary instance; report cases drive the real evalreport compiler
on hand-built bundles (test_evalreport_comparison_kinds.py's builder).
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
REPO = HARNESS.parent
EVALRUN = HARNESS / "evalrun.py"
REGISTRY = REPO / ".claude" / "skills" / "world-edit-compatibility" / "references" / "check-registry.json"
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HARNESS))
sys.path.insert(0, str(REPO / "scripts"))
import test_evalreport_comparison_kinds as K  # noqa: E402  (sets the settings env and imports evalreport)
import evalrun  # noqa: E402

FAILURES: list[str] = []
MAIN, VARIANT, SKYRIM = b"main plugin bytes", b"variant plugin bytes", b"skyrim bytes"


def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok:
        FAILURES.append(label)


def sha(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def clean_env() -> dict:
    env = {k: v for k, v in os.environ.items()
           if k not in ("HOUSECARL_MO2_INSTANCE", "HOUSECARL_MO2_PROFILE", "HOUSECARL_GAME_PATH")}
    env["PYTHONIOENCODING"] = "utf-8"
    return env


def make_instance(root: Path):
    """Main/Mod.esp is the measured copy; Variant/Mod.esp is an author's same-named variant; Copy/Mod.esp is a
    byte-identical second copy."""
    inst, game = root / "instance", root / "game"
    prof = inst / "profiles" / "P"
    prof.mkdir(parents=True)
    (inst / "overwrite").mkdir()
    (game / "Data").mkdir(parents=True)
    (game / "Data" / "Skyrim.esm").write_bytes(SKYRIM)
    for folder, data in (("Main", MAIN), ("Variant", VARIANT), ("Copy", MAIN)):
        (inst / "mods" / folder).mkdir(parents=True)
        (inst / "mods" / folder / "Mod.esp").write_bytes(data)
    (prof / "modlist.txt").write_text("+Variant\n+Copy\n+Main\n", encoding="utf-8")
    (prof / "loadorder.txt").write_text("Skyrim.esm\nMod.esp\n", encoding="utf-8")
    (prof / "plugins.txt").write_text("*Skyrim.esm\n*Mod.esp\n", encoding="utf-8")
    scope = {"complete": True, "configuration": {
        "population": "load-time", "source": "profile:P",
        "plugins": [{"name": "Skyrim.esm", "path": str(game / "Data" / "Skyrim.esm"), "sha256": sha(SKYRIM), "provider": "Data"},
                    {"name": "Mod.esp", "path": str(inst / "mods" / "Main" / "Mod.esp"), "sha256": sha(MAIN), "provider": "Main"}],
        "providers": [{"name": "overwrite", "kind": "overwrite"}, {"name": "Main", "kind": "mod"}, {"name": "Copy", "kind": "mod"},
                      {"name": "Data", "kind": "data"}],
        "overlays": []}}
    (root / "scope.json").write_text(json.dumps(scope), encoding="utf-8")
    c = root / "candidates"
    c.mkdir()
    for stem, mods in (("candidate-00-x-baseline", ["Main"]), ("candidate-01-variant", ["Variant"]),
                       ("candidate-02-copy", ["Copy"]), ("candidate-03-profile-assets", None)):
        doc = {"SchemaVersion": 1, "Name": stem, "BaseProfile": "P", "Plugins": ["Skyrim.esm", "Mod.esp"],
               "Changes": ["fixture"], "Provenance": {"why": "fixture"}}
        if mods is not None:
            doc["Mods"] = mods
        (c / f"{stem}.json").write_text(json.dumps(doc), encoding="utf-8")
    return inst, game


def plan(root: Path, *, with_instance=True):
    out = root / "run"
    if out.exists():
        shutil.rmtree(out)
    inst, game = root / "instance", root / "game"
    args = [sys.executable, str(EVALRUN), "plan", "--registry", str(REGISTRY), "--scope", str(root / "scope.json"),
            "--candidates", str(root / "candidates"), "--out", str(out), "--harness", str(HARNESS),
            "--depth", "standard"]
    if with_instance:
        args += ["--instance", str(inst), "--game", str(game), "--profile", "P"]
    r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace",
                       env=clean_env(), timeout=300)
    doc = json.loads((out / "plan.json").read_text(encoding="utf-8")) if (out / "plan.json").exists() else None
    if doc is None:
        print(r.stdout[-1500:], r.stderr[-1500:])
    return r, doc


def row(doc, arm_id, name="Mod.esp"):
    arm = next((a for a in (doc or {}).get("arms", []) if a.get("armId") == arm_id), {})
    return next((p for p in (arm.get("requested") or {}).get("plugins") or [] if p.get("name") == name), {})


def plan_cases(root: Path):
    make_instance(root)
    r, doc = plan(root)
    check(r.returncode == 0 and doc is not None, f"plan is written (exit {r.returncode})")
    scope_row = row(doc, "baseline")

    # 451: the variant arm requests the variant's bytes, from the folder its mod list puts first.
    v = row(doc, "candidate-01-variant")
    check(v.get("sha256") == sha(VARIANT) and v.get("hashSource") == "instance:Variant",
          f"451: the variant arm requests the variant ({str(v.get('sha256'))[:12]}, {v.get('hashSource')})")
    check(v.get("scopeSha256") == sha(MAIN) and "not the copy the scope measured" in str(v.get("differsFromScope")),
          "451: the row keeps the scope's hash and says it differs")
    check([Path(p["path"]).parent.name for p in v.get("providedBy") or []][:1] == ["Variant"],
          "451: providedBy lists the variant's folder first")

    # 452: a byte-identical copy in another folder keeps the scope's row exactly.
    check(row(doc, "candidate-02-copy") == scope_row and scope_row.get("hashSource") == "scope",
          "452: an identical copy elsewhere leaves the scope row unchanged")

    # 453: an arm without its own Mods reads the scope's view: unchanged.
    check(row(doc, "candidate-03-profile-assets") == scope_row, "453: an arm without Mods keeps the scope row")

    # 455: the run binds the variant arm to the variant: its emit is accepted, the main file's is rejected.
    val = evalrun.Validator(root / "run", doc)
    arm = next(a for a in doc["arms"] if a["armId"] == "candidate-01-variant")

    def mismatch(mod_sha):
        ident = {"plugins": [{"name": "Skyrim.esm", "sha256": sha(SKYRIM), "path": scope_row and row(doc, "baseline", "Skyrim.esm").get("path")},
                             {"name": "Mod.esp", "sha256": mod_sha, "path": v.get("path")}]}
        rej = val._identity_checks(ident, arm, {}, {}, limits=[])
        return [x for x in rej if "Mod.esp was requested at" in str(x.get("detail"))]
    check(not mismatch(sha(VARIANT)), "455: an emit that read the variant is bound to the request")
    check(len(mismatch(sha(MAIN))) == 1, "455: an emit that read the main file is rejected for this arm")

    # 454: no instance: the walk cannot be completed, so the differing arm states nothing rather than the scope hash.
    r2, doc2 = plan(root, with_instance=False)
    u = row(doc2, "candidate-01-variant")
    check(r2.returncode == 0 and u.get("hashSource") == "unstated" and u.get("sha256") is None
          and "--instance, --game and --profile" in str(u.get("unstatedReason")),
          f"454: incomplete walk -> unstated with the reason ({u.get('hashSource')})")
    check(row(doc2, "candidate-03-profile-assets").get("hashSource") == "scope",
          "454: an arm without Mods still keeps the scope row without an instance")
    # 454: a view that only drops a folder below the scope's provider provably reads the same copy, walk or not.
    check(row(doc2, "baseline") == row(doc, "baseline") and row(doc2, "baseline").get("hashSource") == "scope",
          "454: a narrower view that keeps the provider keeps the scope row without a walk")
    check(row(doc2, "candidate-02-copy").get("hashSource") == "unstated",
          "454: a view that drops the scope's provider is unstated without a walk")


def rows(names, shas, unstated=()):
    out = []
    for n in names:
        if n in unstated:
            out.append({"name": n, "sha256": None, "path": None, "hashSource": "unstated"})
        else:
            s = shas.get(n, "0" * 64)
            src = "instance:Variant" if s != "0" * 64 else "scope"
            out.append({"name": n, "sha256": s, "path": f"X:/x/{n}", "hashSource": src})
    return out


def arm(arm_id, names, shas=None, kind="candidate", unstated=()):
    a = K.arm(arm_id, names, kind=kind)
    a["requested"]["plugins"] = rows(names, shas or {}, unstated)
    return a


def report_cases():
    deps, targets = K.DEPS, K.TARGETS
    other = "1" * 64
    # 456: same names and order, one different file -> same-name-replacement, worded with the folder.
    kinds, verdict, blob, _ = K.run_compiler([arm("baseline", deps + targets, kind="baseline"),
                                              arm("variant", deps + targets, {"ModA.esp": other})],
                                             repaired_on=["variant"])
    check(kinds.get("variant") == "same-name-replacement", f"456: kind {kinds.get('variant')!r}")
    check("replacing ModA.esp with another file of the same name (from Variant)" in blob.replace("Replacing", "replacing"),
          "456: the report names the replaced plugin and the folder it came from")
    check("swap a plugin for a different file with the same name" in blob, "456: the plain non-repair sentence")

    # 457: equal bytes stay a control; a replacement with a reorder is not a plain reorder.
    kinds, _, _, _ = K.run_compiler([arm("baseline", deps + targets, kind="baseline"), arm("control", deps + targets)])
    check(kinds.get("control") == "same-order-control", f"457: equal bytes -> {kinds.get('control')!r}")
    kinds, _, _, _ = K.run_compiler([arm("baseline", deps + targets, kind="baseline"),
                                     arm("swap", deps + list(reversed(targets)), {"ModA.esp": other})])
    check(kinds.get("swap") == "mixed", f"457: reorder plus replacement -> {kinds.get('swap')!r}")

    # 458: a hash missing on either side -> bytes not compared, never a control.
    for side, arms_ in (("arm", [arm("baseline", deps + targets, kind="baseline"),
                                 arm("x", deps + targets, unstated=("ModA.esp",))]),
                        ("baseline", [arm("baseline", deps + targets, kind="baseline", unstated=("ModA.esp",)),
                                      arm("x", deps + targets)])):
        kinds, _, blob, plan_ = K.run_compiler(arms_)
        out = next((a for a in (plan_.get("scope") or {}).get("arms") or [] if a.get("armId") == "x"), {})
        check(kinds.get("x") == "bytes-not-compared" and out.get("bytesNotCompared") == ["ModA.esp"],
              f"458: unstated on the {side} side -> {kinds.get('x')!r}, {out.get('bytesNotCompared')}")
        check("bytes were not compared" in blob, f"458: unstated on the {side} side reads 'bytes were not compared'")

    # 459: a replacement next to added patches: all declared -> patch comparison; otherwise not.
    patch, lux = K.PATCH, "Lux - patch.esp"
    base = arm("baseline", deps + targets + [lux], kind="baseline")
    kinds, _, _, _ = K.run_compiler([base, arm("patched", deps + targets + [lux, patch], {lux: other})],
                                    patches=[patch, lux])
    check(kinds.get("patched") == "patch-comparison", f"459: replaced and added are declared -> {kinds.get('patched')!r}")
    kinds, _, _, _ = K.run_compiler([base, arm("patched", deps + targets + [lux, patch], {lux: other})],
                                    patches=[patch])
    check(kinds.get("patched") == "unstated-difference",
          f"459: an undeclared replacement is not a patch comparison -> {kinds.get('patched')!r}")


def main() -> int:
    tmp = Path(tempfile.mkdtemp(prefix="hc-same-name-"))
    try:
        print("=== plan (451-455) ===")
        try:
            plan_cases(tmp / "plan")
        except Exception as ex:
            check(False, f"plan cases raised {type(ex).__name__}: {ex}")
        print("\n=== report (456-459) ===")
        try:
            report_cases()
        except Exception as ex:
            check(False, f"report cases raised {type(ex).__name__}: {ex}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
