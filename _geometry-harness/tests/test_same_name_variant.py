"""A same-named plugin from another folder is planned and reported as itself (cases 451-459), and can be declared
as the patch by its sha256 or its folder without marking the other file of its name (cases 460-479; 475 unused).

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
import struct
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


def tes4(author: str) -> bytes:
    """A TES4-only plugin (HEDR, CNAM): a real header the probes can open; the author keeps two copies' bytes apart."""
    body = b"HEDR" + struct.pack("<H", 12) + struct.pack("<fiI", 1.7, 0, 0x800)
    cnam = author.encode("ascii") + b"\0"
    body += b"CNAM" + struct.pack("<H", len(cnam)) + cnam
    return b"TES4" + struct.pack("<IIIIHH", len(body), 0, 0, 0, 44, 0) + body


MAIN, VARIANT, SKYRIM = tes4("main plugin bytes"), tes4("variant plugin bytes"), b"skyrim bytes"


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
                       ("candidate-02-copy", ["Copy"]), ("candidate-03-profile-assets", None),
                       ("candidate-04-empty-view", [])):
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
    # 459b: dropping the providing folder and adding nothing is still not the scope's copy: the probes then read a lower
    # layer (here the profile's Variant folder, outside the view). Without a walk: unstated; with one: that copy.
    check(row(doc2, "candidate-04-empty-view").get("hashSource") == "unstated",
          "459: an empty view that drops the scope's providing folder is unstated without a walk")
    e = row(doc, "candidate-04-empty-view")
    check(e.get("sha256") == sha(VARIANT) and e.get("hashSource") == "instance:Variant",
          f"459: with a walk the empty view reads the lower layer's copy ({str(e.get('sha256'))[:12]}, {e.get('hashSource')})")


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
    # (item 20) the replaced lux is declared by its bytes: a name alone would stand for the baseline's lux too (D3)
    kinds, _, _, _ = K.run_compiler([base, arm("patched", deps + targets + [lux, patch], {lux: other})],
                                    patches=[patch, {"name": lux, "sha256": other}])
    check(kinds.get("patched") == "patch-comparison", f"459: replaced and added are declared -> {kinds.get('patched')!r}")
    kinds, _, _, _ = K.run_compiler([base, arm("patched", deps + targets + [lux, patch], {lux: other})],
                                    patches=[patch])
    check(kinds.get("patched") == "unstated-difference",
          f"459: an undeclared replacement is not a patch comparison -> {kinds.get('patched')!r}")


# ------------------------------------------------------------------ item 20: a same-name variant declared as the patch
#
# The Sewers shape (lab hashes measured at Gate A): the evaluated mod SkyrimSewers.esp, the author's Capital Whiterun
# compatible file of the same name from its own folder, and Capital Whiterun's SurWR.esp beside it.
SEW, CW = "SkyrimSewers.esp", "SurWR.esp"
MAIN_SHA = "2d2d3e1b78e32affc5b1979c0a165ba15cdcd6c21228ac9600ad030a8c76a47d"
VAR_SHA = "a26477fe028c560bded239b64b30ed03d36c574d952ebef64d51e066e96b1e73"
MAIN_DIR, VAR_DIR = "Skyrim Sewers 4", "Skyrim Sewers 4 Capital Whiterun Expansion compatible esp"
LABEL = f"{SEW} from {VAR_DIR}"
BY_HASH = {"name": SEW, "sha256": VAR_SHA}
BY_FOLDER = {"name": SEW, "folder": VAR_DIR}


def sew_row(which, source):
    """main/variant copy of SkyrimSewers.esp: `scope` (the measured scope's row, a backslash path as the lab's plans
    write it), `instance` (an own-view row as SW1 writes it), or `unstated` (no hash)."""
    sha_, dir_ = (MAIN_SHA, MAIN_DIR) if which == "main" else (VAR_SHA, VAR_DIR)
    if source == "unstated":
        return {"name": SEW, "sha256": None, "path": None, "hashSource": "unstated", "scopeSha256": MAIN_SHA,
                "unstatedReason": "fixture"}
    row_ = {"name": SEW, "sha256": sha_, "path": f"X:/Skyrim Conflict Lab\\mods\\{dir_}\\{SEW}",
            "hashSource": "scope" if source == "scope" else f"instance:{dir_}"}
    if source == "instance":
        row_["scopeSha256"] = MAIN_SHA
    return row_


def with_sew(a, which, source):
    """The arm with its SkyrimSewers.esp row replaced by that copy; every other row a stated scope row."""
    a["requested"]["plugins"] = [sew_row(which, source) if p["name"] == SEW else
                                 {"name": p["name"], "sha256": p.get("sha256") or "0" * 64,
                                  "path": f"X:/x/{p['name']}", "hashSource": "scope"}
                                 for p in a["requested"]["plugins"]]
    return a


def sewers_arms(direction="arm", variant_source="instance", other_source="scope", extra=None):
    """Two setups: the measured one and the same names in the same order with the other copy. direction "arm": the
    variant is the alternative (main in the baseline); "baseline": E3's direction (the variant measured, main as the
    alternative)."""
    names = K.DEPS + [CW, SEW]
    base_which, alt_which = ("main", "variant") if direction == "arm" else ("variant", "main")
    base_src = other_source if direction == "arm" else variant_source
    alt_src = variant_source if direction == "arm" else other_source
    alt_id = "cw-variant" if direction == "arm" else "main"
    alt_names = list(names) if extra is None else extra(list(names))
    return ([with_sew(K.arm("baseline", names, kind="baseline"), base_which, base_src),
             with_sew(K.arm(alt_id, alt_names), alt_which, alt_src)], alt_id)


def decls(plan_):
    return list((plan_.get("scope") or {}).get("patchDeclarations") or [])


def copies(plan_, i=0):
    d = decls(plan_)
    return d[i].get("copies") if len(d) > i else None


def refusal(fn):
    """The InputRefused sentence fn raised, or None."""
    try:
        fn()
    except K.R.InputRefused as ex:
        return str(ex)
    return None


def declared_cases():
    t = [SEW]
    # 460: the variant declared by name and sha256 is the patch: a patch comparison that may claim a repair.
    arms_, alt = sewers_arms()
    kinds, verdict, blob, plan_ = K.run_compiler(arms_, repaired_on=[alt], patches=[BY_HASH], targets=t)
    check(kinds.get(alt) == "patch-comparison", f"460: variant declared by hash -> {kinds.get(alt)!r}")
    check("resolves some measured conflicts" in verdict, "460: the declared variant's reduction is read as a repair")

    # 461: the reader is told which file is the patch, with its folder, and never that it is not reported as a fix.
    rd = plan_.get("reader") or {}
    check(f"The patch evaluated is {LABEL}, loaded in place of the evaluated mod's file of the same name for comparison."
          in blob, "461: the scope line names the variant with its folder, in place of the mod's file")
    check(f"Replacing {SEW} with the patch of the same name from {VAR_DIR}" in blob,
          "461: the setup is worded as replacing the mod's file with the patch of the same name")
    check("not reported as a fix" not in blob, "461: no 'not reported as a fix' for a declared patch comparison")
    check(list(rd.get("suppliedPatches") or []) == [SEW], f"461: suppliedPatches {rd.get('suppliedPatches')!r}")

    # 462: only the variant's copy is marked; the baseline's copy stays the unpatched prediction.
    val = rd.get("validation") or {}
    check(copies(plan_) == [{"armId": alt, "sha256": VAR_SHA, "folder": VAR_DIR}],
          f"462: the declaration marks the variant's copy only ({copies(plan_)!r})")
    check(val.get("status") != "no-patch" and val.get("withPatchArm") == alt and val.get("withoutPatchArm") == "baseline"
          and (rd.get("prediction") or {}).get("predictionArm") == "baseline",
          f"462: validation {val.get('status')!r}, with {val.get('withPatchArm')!r}, without {val.get('withoutPatchArm')!r}")

    # 463: the same, declared by the folder that provides it (an own-view `instance:<folder>` row).
    kinds, _, _, plan_ = K.run_compiler(arms_, patches=[BY_FOLDER], targets=t)
    check(kinds.get(alt) == "patch-comparison", f"463: variant declared by folder -> {kinds.get(alt)!r}")
    check(copies(plan_) == [{"armId": alt, "sha256": VAR_SHA, "folder": VAR_DIR}],
          f"463: the folder declaration marks the variant's copy only ({copies(plan_)!r})")

    # 464: E3's direction: the variant measured in the baseline (a scope row; its folder only in the path), the main
    # file as the alternative. The patch is removed in place: the alternative is the unpatched prediction.
    arms_e3, alt_e3 = sewers_arms(direction="baseline", variant_source="scope", other_source="instance")
    kinds, _, _, plan_ = K.run_compiler(arms_e3, patches=[BY_FOLDER], targets=t)
    val = (plan_.get("reader") or {}).get("validation") or {}
    check(kinds.get(alt_e3) == "patch-comparison", f"464: E3 direction by folder -> {kinds.get(alt_e3)!r}")
    check(val.get("withPatchArm") == "baseline" and val.get("withoutPatchArm") == alt_e3
          and ((plan_.get("reader") or {}).get("prediction") or {}).get("predictionArm") == alt_e3,
          f"464: with {val.get('withPatchArm')!r}, without {val.get('withoutPatchArm')!r}")

    # 465: declared by name alone: the name stands for two files, so neither is the patch, and the reader is told so.
    kinds, _, blob, plan_ = K.run_compiler(arms_, patches=[SEW], targets=t)
    rd = plan_.get("reader") or {}
    check(kinds.get(alt) == "same-name-replacement", f"465: name only -> {kinds.get(alt)!r}")
    check(list(rd.get("suppliedPatches") or []) == [] and "The patch evaluated is" not in blob,
          f"465: no patch is named as evaluated ({rd.get('suppliedPatches')!r})")
    check(f"{SEW} is declared as a patch by name only, but the setups load different files with that name, so neither "
          f"is read as the patch; declare it with its sha256 or its folder to compare it as one." in blob,
          "465: the ambiguity is said in one plain sentence")
    check(len(decls(plan_)) == 1 and decls(plan_)[0].get("ambiguous") is True and copies(plan_) == [],
          f"465: the name-only declaration marks no copy ({decls(plan_)!r})")

    # 467: a qualified declaration that matches nothing is refused with its reason, never ignored.
    why = refusal(lambda: K.run_compiler(arms_, patches=[{"name": SEW, "sha256": "f" * 64}], targets=t))
    check(bool(why) and why.startswith("patch-declaration-unmatched:") and SEW in why and "ffffffffffff" in why,
          f"467: a hash matching no copy is refused ({why!r})")
    why = refusal(lambda: K.run_compiler(arms_, patches=[{"name": SEW, "folder": "No Such Folder"}], targets=t))
    check(bool(why) and why.startswith("patch-declaration-unmatched:") and "No Such Folder" in why,
          f"467: a folder matching no copy is refused ({why!r})")
    arms_u, _ = sewers_arms(variant_source="unstated")
    why = refusal(lambda: K.run_compiler(arms_u, patches=[BY_HASH], targets=t))
    check(bool(why) and why.startswith("patch-declaration-unmatched:") and "unstated" in why,
          f"467: with the variant's hash unstated the refusal says so ({why!r})")

    # 468: a malformed declaration is refused.
    why = refusal(lambda: K.run_compiler(arms_, patches=[{"name": SEW, "sha256": VAR_SHA[:12]}], targets=t))
    check(bool(why) and why.startswith("patch-declaration-invalid:") and "64" in why,
          f"468: a 12-character hash is refused ({why!r})")
    why = refusal(lambda: K.run_compiler(arms_, patches=[{"sha256": VAR_SHA}], targets=t))
    check(bool(why) and why.startswith("patch-declaration-invalid:"), f"468: a declaration without a name is refused ({why!r})")

    # 469: --declare-patch's spellings (carried in settings as given) reach the classifier.
    for spelling in (f"{SEW}=sha256:{VAR_SHA}", f"{SEW}=folder:{VAR_DIR}"):
        kinds, _, _, _ = K.run_compiler(arms_, declared=[spelling], targets=t)
        check(kinds.get(alt) == "patch-comparison", f"469: --declare-patch {spelling[:40]}... -> {kinds.get(alt)!r}")
    h = subprocess.run([sys.executable, str(HARNESS / "evalreport.py"), "plan", "--help"], capture_output=True,
                       text=True, encoding="utf-8", errors="replace", env=clean_env(), timeout=120)
    help_ = " ".join(h.stdout.split())
    check("=sha256:" in help_ and "=folder:" in help_, "469: --declare-patch's help names both spellings")

    # 470: an arm's provenance declares the patch by its bytes (dict entries used to be dropped silently).
    arms_p, _ = sewers_arms()
    arms_p[1]["provenance"] = {"patches": [BY_HASH]}
    kinds, _, _, _ = K.run_compiler(arms_p, targets=t)
    check(kinds.get(alt) == "patch-comparison", f"470: provenance dict declaration -> {kinds.get(alt)!r}")

    # 471: case 459's shape with lux declared by name: lux is two files, so the arm is an unstated difference, and the
    # sentence names lux (the undeclared change), not the declared patch it was added with.
    deps, targets = K.DEPS, K.TARGETS
    patch, lux, other = K.PATCH, "Lux - patch.esp", "1" * 64
    base = arm("baseline", deps + targets + [lux], kind="baseline")
    kinds, _, blob, _ = K.run_compiler([base, arm("patched", deps + targets + [lux, patch], {lux: other})],
                                       patches=[patch, lux])
    check(kinds.get("patched") == "unstated-difference", f"471: lux by name -> {kinds.get('patched')!r}")
    check(f"One tested setup differs by {lux}, which is not declared as a patch" in blob
          and f"differs by {patch}" not in blob, "471: the unstated sentence names lux, not the declared patch")

    # 473: a mixed arm that changes the declared variant: the reader is told the declared patch changed, with its folder.
    arms_m, alt_m = sewers_arms(extra=lambda ns: [n for n in ns if n != CW] + ["Extra.esp"])
    kinds, _, blob, _ = K.run_compiler(arms_m, patches=[BY_HASH], targets=t)
    check(kinds.get(alt_m) == "mixed", f"473: kind {kinds.get(alt_m)!r}")
    check(f"The tested setup changes the declared patch {LABEL} together with another plugin, so it is not evaluated on "
          f"its own." in blob and "No separate patch plugin was declared" not in blob,
          "473: the mixed sentence names the declared variant; no 'No separate patch plugin was declared'")

    # 476: an ordinary added patch declared by name (one file) keeps today's reading and its bare name.
    kinds, _, blob, _ = K.run_compiler([K.arm("baseline", deps + targets, kind="baseline"),
                                        K.arm("with-patch", deps + targets + [patch])], patches=[patch])
    check(kinds.get("with-patch") == "patch-comparison"
          and f"The patch evaluated is {patch}, added to the measured load order for comparison." in blob,
          f"476: ordinary declared patch unchanged ({kinds.get('with-patch')!r})")

    # 477: both copies declared by their bytes (two versions of a patch): no side is without the patch.
    kinds, _, _, plan_ = K.run_compiler(arms_, patches=[{"name": SEW, "sha256": MAIN_SHA}, BY_HASH], targets=t)
    check(kinds.get(alt) == "same-name-replacement", f"477: both copies declared -> {kinds.get(alt)!r}")
    check(copies(plan_, 0) == [{"armId": "baseline", "sha256": MAIN_SHA, "folder": MAIN_DIR}]
          and copies(plan_, 1) == [{"armId": alt, "sha256": VAR_SHA, "folder": VAR_DIR}],
          f"477: each declaration marks its own copy ({decls(plan_)!r})")

    # 478 (C1a): an ordinary added patch declared by name whose row is unstated keeps today's reading and bare name.
    pa = K.arm("with-patch", deps + targets + [patch])
    pa["requested"]["plugins"] = [dict(p, sha256=None, hashSource="unstated", path=None) if p["name"] == patch else p
                                  for p in pa["requested"]["plugins"]]
    kinds, _, blob, _ = K.run_compiler([K.arm("baseline", deps + targets, kind="baseline"), pa], patches=[patch])
    check(kinds.get("with-patch") == "patch-comparison"
          and f"The patch evaluated is {patch}, added to the measured load order for comparison." in blob,
          f"478: unstated added patch by name unchanged ({kinds.get('with-patch')!r})")


def prediction_cases():
    """Readers of the finding rows (P = test_evalreport_prediction's builder): each row's own setup decides whether the
    file it names is the declared copy."""
    import test_evalreport_prediction as P
    B = P.BASE
    fk = (f"000701:{B}", f"000702:{B}")

    def p_arms(alt_source="instance", targets=(SEW,)):
        names = [B, CW, SEW]
        return [with_sew(P.arm("baseline", names, kind="baseline"), "main", "scope"),
                with_sew(P.arm("cw-variant", names), "variant", alt_source)]

    # without the variant CW's change to two records is lost to Sewers; the variant still wins one, gives up the other
    p_rows = [P.lost_row(CW, SEW, k, "LocationReference", f"c{i}", f"s{i}", "v") for i, k in enumerate(fk)]
    w_rows = [P.lost_row(CW, SEW, fk[0], "LocationReference", "c0", "s0", "v")]
    enr = {(P.RECORDS, "baseline"): p_rows, (P.RECORDS, "cw-variant"): w_rows}
    co = {(P.RECORDS, "baseline"): 2, (P.RECORDS, "cw-variant"): 1}
    res = {(P.CONTACT, "baseline"): [], (P.CONTACT, "cw-variant"): []}

    def rec(plan_, arm_id):
        return [f for f in plan_.get("findings") or [] if f.get("family") == "records" and f.get("armId") == arm_id]

    # 466: declared by hash: the variant's wins are the patch's choices, the baseline's never; the validation reads the
    # field the variant still wins as kept and the one it gives up as resolved.
    plan_, md, _ = P.compile_and_render(p_arms(), res, [SEW], patches=[BY_HASH], count_only=co, enrichment=enr)
    val = (plan_.get("reader") or {}).get("validation") or {}
    check(any(f.get("readerPatchChoice") for f in rec(plan_, "cw-variant")),
          "466: the variant's won lost edit is the patch's choice")
    check(not any(f.get("readerPatchChoice") for f in rec(plan_, "baseline")),
          "466: the baseline's lost edits to the main file are never patch choices")
    check(val.get("withPatchArm") == "cw-variant" and val.get("kept") == 1 and val.get("hits") == 1,
          f"466: validation kept {val.get('kept')}, hits {val.get('hits')}, with {val.get('withPatchArm')!r}")
    # 472: the baseline's clash between the mod and Capital Whiterun is set aside as one mod's; the variant's is a
    # clash with the patch and stays listed.
    check([f.get("readerDetailAside") for f in rec(plan_, "baseline")] == ["singleMod"]
          and [f.get("readerDetailAside") for f in rec(plan_, "cw-variant")] == [None],
          f"472: asides baseline {[f.get('readerDetailAside') for f in rec(plan_, 'baseline')]}, "
          f"variant {[f.get('readerDetailAside') for f in rec(plan_, 'cw-variant')]}")
    # 466: by name alone no row anywhere is a patch choice.
    plan_n, _, _ = P.compile_and_render(p_arms(), res, [SEW], patches=[SEW], count_only=co, enrichment=enr)
    check(not any(f.get("readerPatchChoice") for f in plan_n.get("findings") or []),
          "466: declared by name alone, no row is a patch choice")

    # 474: the reader's document lists the variant by its folder, not the same file name twice.
    import readerdoc as RD
    words = json.loads((REPO / ".claude/skills/evaluation-reporting/references/report-priorities.json")
                       .read_text(encoding="utf-8"))["readerDoc"]
    plan2, _, _ = P.compile_and_render(p_arms(), res, [SEW, CW], patches=[BY_HASH], count_only=co, enrichment=enr)
    try:
        doc_md = RD.build(plan2, words, {}, None, None, "show", None)[0]
    except Exception as ex:                                    # a refusal or crash fails here, with its reason
        doc_md = f"readerdoc raised {type(ex).__name__}: {ex}"
    files = next((ln for ln in doc_md.splitlines() if ln.startswith("Files:")), doc_md[:160])
    check(LABEL in files and f"{SEW}, {SEW}" not in files, f"474: files line {files!r}")

    # 479 (C1b): both setups carry the name and the variant's bytes are unstated: by name alone neither copy is marked.
    plan_u, _, _ = P.compile_and_render(p_arms(alt_source="unstated"), res, [SEW], patches=[SEW], count_only=co,
                                        enrichment=enr)
    kinds_u = {a["armId"]: a.get("comparisonKind") for a in (plan_u.get("scope") or {}).get("arms") or []}
    check(kinds_u.get("cw-variant") == "bytes-not-compared", f"479: kind {kinds_u.get('cw-variant')!r}")
    check(not any(f.get("readerPatchChoice") for f in plan_u.get("findings") or []),
          "479: no row of either setup is a patch choice")
    d_ = decls(plan_u)
    check(len(d_) == 1 and d_[0].get("ambiguous") is True and d_[0].get("copies") == [],
          f"479: the name-only declaration marks no copy ({d_!r})")


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
        print("\n=== declared same-name patch (460-479) ===")
        for fn in (declared_cases, prediction_cases):
            try:
                fn()
            except Exception as ex:
                import traceback
                traceback.print_exc()
                check(False, f"{fn.__name__} raised {type(ex).__name__}: {ex}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    if FAILURES:
        print(f"\n{len(FAILURES)} FAILED")
        return 1
    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
