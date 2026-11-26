"""Known answers for a lab run's pictures across a rebuild, and for the blind observations' picture comparison.

Part 1 (cases 1xx): report-plan, capture and report are bound to what the programs they start ARE (viewprobe, runcontrol,
evalcontrol: the bytes of the folder each child runs, built without a commit stamp, and the build's Data pieces the
controllers read), never to one build of them; a real change runs the stage again, and a missing, unreadable or
overridden identity is never "unchanged". Part 2 (cases 2xx): a redone capture whose frames match the described frames
within the stated tolerance (every channel within 1 level, at most 0.01 % of the pixels, the same recorded render
arguments) carries the banked observations; anything else asks for them again.

Every case goes through what the pipeline calls: evalpipeline.cmd_run with stand-in stage bodies (the real
stage_tools, StageLedger.decide and record), the real stage_image_observations, reusable_capture and
capture_acquire.capture_tool_identity.

Needs Pillow and numpy: `python integration/housecarl/tests/test_lab_pictures_reuse.py`. EVALPIPELINE_PATH and
CAPTURE_ACQUIRE_PATH, if set, load those modules from other files (the sabotage arms use them).
"""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import traceback
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import skywright_paths as SW  # noqa: E402  SkyWright's tools on sys.path
import buildlayout  # noqa: E402
import toolidentity  # noqa: E402


def _load(name, env, default):
    path = os.environ.get(env) or str(default)
    try:
        spec = importlib.util.spec_from_file_location(name, path)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod, None
    except Exception as ex:  # noqa: BLE001
        return None, f"{type(ex).__name__}: {ex}"


EP, EP_ERROR = _load("evalpipeline_lpr", "EVALPIPELINE_PATH", HERE.parent / "evalpipeline.py")
CA, CA_ERROR = _load("capture_acquire_lpr", "CAPTURE_ACQUIRE_PATH", HERE.parent / "capture_acquire.py")
PASSED, FAILURES = [], []


def check(name, fn):
    try:
        if EP is None:
            raise AssertionError(f"evalpipeline.py is not loadable: {EP_ERROR}")
        fn()
        PASSED.append(name)
        print(f"   PASS  {name}")
    except Exception as e:  # noqa: BLE001
        FAILURES.append(name)
        print(f"   FAIL  {name}: {type(e).__name__}: {e}")
        if not isinstance(e, AssertionError):
            traceback.print_exc()


# =============================================================== Part 1: the programs a stage is bound to

AFTER_BUNDLE = ["report-plan", "capture", "image-observations", "report"]


def fake_build(t: Path) -> dict:
    """A fake app build (marker, Data pieces, viewprobe and runcontrol) and a fake evalcontrol folder; returns the paths
    a machine file names them by."""
    root = Path(t) / "fakebuild"
    spec = buildlayout.plant_marker(root)
    for key in ("corpus", "registry", "severity", "scenePolicy"):
        p = root / spec[key]
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(json.dumps({"piece": key}), encoding="utf-8")
    progs = {}
    for name in ("viewprobe", "runcontrol"):
        exe = buildlayout.program_under(root, name)
        exe.parent.mkdir(parents=True, exist_ok=True)
        exe.write_bytes(b"MZ apphost " + name.encode())
        (exe.parent / f"{name}.dll").write_bytes(b"IL of " + name.encode())
        (exe.parent / f"{name}.runtimeconfig.json").write_text("{}", encoding="utf-8")
        progs[name] = exe
    ev = Path(t) / "fakemodule" / "Tools" / "evalcontrol"
    ev.mkdir(parents=True)
    (ev / "evalcontrol.exe").write_bytes(b"MZ apphost evalcontrol")
    (ev / "evalcontrol.dll").write_bytes(b"IL of evalcontrol")
    (ev / "ai-checks.json").write_text("{}", encoding="utf-8")
    progs["evalcontrol"] = ev / "evalcontrol.exe"
    return {"root": root, "spec": spec, **progs}


def machine_of(fb: dict, *names) -> dict:
    return {n: str(fb[n]) for n in names}


def run_pipeline(t, machine, rounds, between=None, during=None, env=None):
    """cmd_run once per round with stand-in bodies for every stage (the real stage_tools, decide and record). Before
    round i (i >= 1) `between(i)` runs; `during(stage)` runs inside a stage body. Returns [(exit, stages run, stdout)];
    an exception out of cmd_run is returned as ("raised", repr, traceback)."""
    calls = []

    class FakeCtx:
        def __init__(self, entry, machine_path, out):
            self.entry = {"evaluation": {"budgetMinutes": 5}}
            self.machine, self.out, self.id = dict(machine), Path(out), "m"

        def resolve(self):
            return {"arms": [], "subject": ["S.esp"], "keys": []}

    def light(name):
        def body(ctx, res, sdir):
            calls.append(name)
            if during:
                during(name)
            f = sdir / f"{name}.json"
            f.write_text("{}", encoding="utf-8")
            return {}, [f]
        return body

    saved = EP.Context, dict(EP.STAGE_BODIES), EP.MEASURE
    saved_env = {k: os.environ.get(k) for k in list((env or {}).keys()) + ["HOUSECARL_RUNLOG_DIR"]}
    out = Path(t) / "run"
    got = []
    try:
        os.environ["HOUSECARL_RUNLOG_DIR"] = str(Path(t) / "runlog")
        for k, v in (env or {}).items():
            os.environ[k] = v
        EP.Context = FakeCtx
        EP.STAGE_BODIES.update({n: light(n) for n in EP.STAGES})
        EP.MEASURE = lambda ctx, through: None
        a = types.SimpleNamespace(entry="e.json", machine=None, out=str(out), through="report")
        for i in range(rounds):
            if i and between:
                between(i)
            calls.clear()
            buf = io.StringIO()
            try:
                with contextlib.redirect_stdout(buf):
                    rc = EP.cmd_run(a)
                got.append((rc, list(calls), buf.getvalue()))
            except Exception as ex:  # noqa: BLE001
                got.append(("raised", repr(ex), traceback.format_exc()))
    finally:
        EP.Context, EP.MEASURE = saved[0], saved[2]
        EP.STAGE_BODIES.clear()
        EP.STAGE_BODIES.update(saved[1])
        for k, v in saved_env.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
    return got


def touch_same_bytes(folder: Path):
    """An unchanged rebuild: every file written again with the same bytes (new modification times)."""
    for f in Path(folder).rglob("*"):
        if f.is_file():
            data = f.read_bytes()
            f.unlink()
            f.write_bytes(data)
            st = f.stat()
            os.utime(f, ns=(st.st_atime_ns, st.st_mtime_ns + 5_000_000_000))


def case_101_an_unchanged_rebuild_reuses_report_plan_capture_and_report():
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        m = machine_of(fb, "viewprobe", "runcontrol", "evalcontrol")

        def rebuild(i):
            for n in ("viewprobe", "runcontrol", "evalcontrol"):
                touch_same_bytes(fb[n].parent)
        got = run_pipeline(t, m, 2, between=rebuild)
    assert got[0][0] == 0 and set(AFTER_BUNDLE) <= set(got[0][1]), f"the first run did not run the stages: {got[0][:2]}"
    assert got[1][0] == 0 and got[1][1] == [], (
        f"an unchanged rebuild (same bytes, new times) ran {got[1][1]} again; report-plan, capture and report must be reused")
    for s in ("report-plan", "capture", "report"):
        assert f"{s}: reused" in got[1][2], f"the second run does not print '{s}: reused'"


def _stamped(data: bytes) -> list:
    pats = [rb"\+[0-9a-f]{40}", re.escape("+".encode("utf-16-le")) + rb"(?:[0-9a-f]\x00){40}"]
    return [m.group(0)[:24] for p in pats for m in re.finditer(p, data)]


def case_102_the_built_programs_carry_no_commit_stamp():
    """F2: the programs' own dlls AND those built from the pin carry no '+<commit>' informational suffix, nor does the
    apphost's version resource; the same code from another commit is then the same bytes."""
    vp, rc = buildlayout.program_dir("viewprobe"), buildlayout.program_dir("runcontrol")
    ev = SW.tool("evalcontrol").parent
    files = [vp / "viewprobe.dll", vp / "viewprobe.exe", vp / "SkyWright.Engine.dll", vp / "housecarl-core.dll",
             rc / "runcontrol.dll", rc / "runcontrol.exe", rc / "housecarl-mcp.dll", rc / "housecarl-nexus.dll",
             ev / "evalcontrol.dll", ev / "evalcontrol.exe"]
    missing = [str(f) for f in files if not f.is_file()]
    assert not missing, f"not built (scripts/build.ps1): {missing}"
    stamped = {f.name: _stamped(f.read_bytes()) for f in files}
    stamped = {k: v for k, v in stamped.items() if v}
    assert not stamped, f"these built files carry a commit stamp, so an unchanged rebuild changes their bytes: {stamped}"


def case_117_the_built_debug_files_carry_no_commit():
    """A program's dll names its debug file by an ID derived from the debug file's bytes, so a commit written into the
    debug file (source link) changes the dll on every commit: no pdb beside the programs may hold one."""
    head = subprocess.run(["git", "-C", str(SW.CHECKOUT), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    pin = subprocess.run(["git", "-C", str(SW.HOUSECARL), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    vp, rc = buildlayout.program_dir("viewprobe"), buildlayout.program_dir("runcontrol")
    ev = SW.tool("evalcontrol").parent
    pdbs = sorted(p for d in (vp, rc, ev) for p in d.glob("*.pdb"))
    assert pdbs, "no debug files found beside the programs; build first (scripts/build.ps1)"
    hits = sorted({f"{p.parent.name}/{p.name}" for p in pdbs for c in (head, pin) if c and c.encode() in p.read_bytes()})
    assert not hits, f"these debug files hold a commit id, so each commit changes the programs' bytes: {hits}"


def case_103_a_real_renderer_change_runs_capture_again():
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        m = machine_of(fb, "viewprobe", "runcontrol", "evalcontrol")
        got = run_pipeline(t, m, 2, between=lambda i: (fb["viewprobe"].parent / "viewprobe.dll").write_bytes(b"IL changed"))
    assert got[1][1] == ["capture", "image-observations", "report"], (
        f"after a change to viewprobe.dll the second run ran {got[1][1]}; capture and what follows must run again, "
        f"report-plan (which starts no renderer) must be reused")


def case_104_a_real_controller_change_runs_report_plan_capture_and_report_again():
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        m = machine_of(fb, "viewprobe", "runcontrol", "evalcontrol")
        got = run_pipeline(t, m, 2, between=lambda i: (fb["runcontrol"].parent / "runcontrol.dll").write_bytes(b"IL changed"))
    assert got[1][1] == AFTER_BUNDLE, f"after a change to runcontrol.dll the second run ran {got[1][1]}, not {AFTER_BUNDLE}"


def case_105_an_evalcontrol_change_runs_report_plan_capture_and_report_again():
    """report-plan, capture (its re-plans) and report start evalcontrol (evalreport's verify, the media bundle)."""
    for changed in ("evalcontrol.dll", "ai-checks.json"):
        with tempfile.TemporaryDirectory() as t:
            fb = fake_build(Path(t))
            m = machine_of(fb, "viewprobe", "runcontrol", "evalcontrol")
            got = run_pipeline(t, m, 2, between=lambda i: (fb["evalcontrol"].parent / changed).write_bytes(b"changed"))
        assert got[1][1] == AFTER_BUNDLE, f"after a change to evalcontrol's {changed} the second run ran {got[1][1]}, not {AFTER_BUNDLE}"


def case_106_a_missing_program_is_never_unchanged():
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        m = machine_of(fb, "runcontrol", "evalcontrol")
        m["viewprobe"] = str(Path(t) / "nowhere" / "viewprobe.exe")
        got = run_pipeline(t, m, 2)
    assert got[1][0] != "raised", f"cmd_run raised: {got[1][1]}"
    assert "capture" in got[1][1], f"with viewprobe absent on both runs the capture was reused ({got[1][1]}): absent == absent is not 'unchanged'"
    assert "viewprobe" in got[1][2], "the run does not say which program's identity is missing"


def case_107_an_unreadable_program_is_never_unchanged_and_never_a_traceback():
    real = toolidentity.file_sha256

    def locked(p):
        if Path(p).name == "viewprobe.dll":
            raise toolidentity.Unreadable(Path(p), PermissionError("locked"))
        return real(p)
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        m = machine_of(fb, "viewprobe", "runcontrol", "evalcontrol")
        toolidentity.file_sha256 = locked
        try:
            got = run_pipeline(t, m, 2)
        finally:
            toolidentity.file_sha256 = real
    assert got[0][0] != "raised" and got[1][0] != "raised", f"an unreadable viewprobe.dll raised out of the run: {got[-1][1]}"
    assert "capture" in got[1][1], f"with viewprobe.dll unreadable on both runs the capture was reused ({got[1][1]})"
    assert "viewprobe" in got[1][2], "the run does not say which program could not be read"


def case_108_the_stage_is_bound_to_the_runcontrol_its_children_run():
    """No runcontrol in the machine file, HOUSECARL_RUNCONTROL names one: the children run that one, so it is bound."""
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        m = machine_of(fb, "viewprobe", "evalcontrol")
        got = run_pipeline(t, m, 2, env={"HOUSECARL_RUNCONTROL": str(fb["runcontrol"])},
                           between=lambda i: (fb["runcontrol"].parent / "runcontrol.dll").write_bytes(b"IL changed"))
    assert got[1][1] == AFTER_BUNDLE, (
        f"the runcontrol HOUSECARL_RUNCONTROL names changed and the second run ran {got[1][1]}: the stages are bound to "
        f"another runcontrol than the one their children run")


def case_109_a_program_changed_during_the_stage_leaves_no_reusable_receipt():
    """viewprobe is replaced while the capture runs and put back before the next run: the receipt was made by two
    builds and must not be reused."""
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        dll = fb["viewprobe"].parent / "viewprobe.dll"
        orig = dll.read_bytes()
        m = machine_of(fb, "viewprobe", "runcontrol", "evalcontrol")
        flips = {"n": 0}

        def during(stage):
            if stage == "capture" and flips["n"] == 0:
                flips["n"] = 1
                dll.write_bytes(b"IL rebuilt mid-stage")
        got = run_pipeline(t, m, 2, during=during, between=lambda i: dll.write_bytes(orig))
    assert "capture" in got[1][1], (
        f"viewprobe changed during the capture and back; the second run reused the capture ({got[1][1]})")


def case_110_a_change_to_the_builds_data_runs_the_controller_stages_again():
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        m = machine_of(fb, "viewprobe", "runcontrol", "evalcontrol")
        reg = fb["root"] / fb["spec"]["registry"]
        got = run_pipeline(t, m, 2, between=lambda i: reg.write_text('{"changed": true}', encoding="utf-8"))
    assert got[1][1] == AFTER_BUNDLE, f"after the build's check registry changed the second run ran {got[1][1]}, not {AFTER_BUNDLE}"


def case_111_a_receipt_bound_the_old_way_is_not_reused():
    """lab3's shape: a request result whose toolIdentity hashes viewprobe.exe (the apphost) is captured again."""
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        old = {"capture_acquire.py": toolidentity.file_sha256(HERE.parent / "capture_acquire.py"),
               "capture_points.py": toolidentity.file_sha256(HERE.parent / "capture_points.py"),
               "viewprobe": toolidentity.file_sha256(fb["viewprobe"])}
        qdir = Path(t) / "capture-old"
        (qdir / "sequence").mkdir(parents=True)
        (qdir / "sequence" / "capture-result.json").write_text(json.dumps(
            {"request": "capture-old", "status": "captured", "toolIdentity": old}), encoding="utf-8")
        now = CA.capture_tool_identity(str(fb["viewprobe"]))
        got = EP.reusable_capture({"id": "capture-old"}, qdir, now)
    assert got is None, "a request result bound to the apphost's hash (the old shape) was reused"


def case_112_the_per_request_identity_sees_a_renderer_code_change_the_apphost_does_not_show():
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        before = CA.capture_tool_identity(str(fb["viewprobe"]))
        qdir = Path(t) / "capture-a"
        (qdir / "sequence").mkdir(parents=True)
        (qdir / "sequence" / "capture-result.json").write_text(json.dumps(
            {"request": "capture-a", "status": "captured", "toolIdentity": before}), encoding="utf-8")
        (fb["viewprobe"].parent / "viewprobe.dll").write_bytes(b"IL changed, apphost the same")
        after = CA.capture_tool_identity(str(fb["viewprobe"]))
        reused = EP.reusable_capture({"id": "capture-a"}, qdir, after)
    assert before != after, "viewprobe.dll changed and the per-request capture identity did not"
    assert reused is None, "a request rendered by the old viewprobe.dll was reused"


def _provenance_files():
    """Every program folder this build writes, by the layouts' own markers."""
    app = buildlayout.root()
    mod = SW.build_root()
    folders = [p for base in (app / "Probes", app / "Tools", mod / "Probes", mod / "Tools") if base.is_dir()
               for p in sorted(base.iterdir()) if p.is_dir()]
    return folders


def case_113_every_program_folder_records_the_commit_and_dirtiness_it_was_built_from():
    """F1: provenance/build.json in every program folder names HEAD and whether the tree was dirty."""
    head = subprocess.run(["git", "-C", str(SW.CHECKOUT), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    folders = _provenance_files()
    assert folders, "no program folders found; build first (scripts/build.ps1)"
    bad = []
    for f in folders:
        rec = f / "provenance" / "build.json"
        try:
            doc = json.loads(rec.read_text(encoding="utf-8"))
        except (OSError, ValueError) as ex:
            bad.append(f"{f.name}: {type(ex).__name__}")
            continue
        if doc.get("commit") != head or not isinstance(doc.get("dirty"), bool):
            bad.append(f"{f.name}: {doc}")
    assert not bad, f"{len(bad)} of {len(folders)} program folders do not record HEAD {head[:12]} and a dirty flag: {bad[:6]}"


def case_114_the_provenance_record_is_outside_the_package_identity():
    """F1, the other way: rewriting only the provenance record (another commit, dirty) leaves the digest unchanged."""
    folder_ = buildlayout.program_dir("viewprobe")
    # wherever the build writes the record (a build.json naming a commit) is where its identity is tested
    found = [p for p in sorted(folder_.rglob("build.json")) if '"commit"' in p.read_text(encoding="utf-8", errors="replace")]
    real = found[0] if found else folder_ / "provenance" / "build.json"
    rel = real.relative_to(folder_)
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        folder = fb["viewprobe"].parent
        rec = folder / rel
        rec.parent.mkdir(parents=True, exist_ok=True)
        rec.write_text(json.dumps({"commit": "a" * 40, "dirty": False}), encoding="utf-8")
        d1 = toolidentity.package(folder)["digest"]
        rec.write_text(json.dumps({"commit": "b" * 40, "dirty": True}), encoding="utf-8")
        d2 = toolidentity.package(folder)["digest"]
    assert real.is_file(), f"the build writes no provenance record at {real}"
    assert d1 == d2, f"the provenance record at {rel} is inside the package identity: changing it changed the digest"


def case_115_a_layout_override_in_the_environment_is_never_unchanged():
    """F4: any WB_* variable (a superset of the overrides CheckoutLayout honours) makes the controllers' identity unknown."""
    with tempfile.TemporaryDirectory() as t:
        fb = fake_build(Path(t))
        m = machine_of(fb, "viewprobe", "runcontrol", "evalcontrol")
        got = run_pipeline(t, m, 2, env={"WB_REGISTRY": str(fb["root"] / fb["spec"]["registry"])})
    assert got[1][1] == AFTER_BUNDLE, f"with WB_REGISTRY set the second run ran {got[1][1]}, not {AFTER_BUNDLE}"
    assert "WB_REGISTRY" in got[1][2], "the run does not name the override that keeps it from reusing"


def _provenance_target(out: Path, env_changes: dict) -> dict | str:
    """The build's provenance target alone (SkyWrightProvenance), on a small program of this checkout, writing into
    `out`; returns the record, or a sentence when the target did not write one."""
    dotnet = shutil.which("dotnet")
    proj = SW.CHECKOUT / "_geometry-harness" / "runcontrol" / "runcontrol.csproj"
    env = dict(os.environ, **env_changes)
    r = subprocess.run([dotnet, "msbuild", str(proj), "-t:SkyWrightProvenance", "-nologo", "-v:q",
                        f"-p:SkyWrightProvenanceDir={out}\\"], capture_output=True, text=True, env=env, cwd=str(SW.CHECKOUT))
    rec = out / "build.json"
    if not rec.is_file():
        return f"no record written (exit {r.returncode}): {(r.stdout + r.stderr)[-400:]}"
    return json.loads(rec.read_text(encoding="utf-8"))


def case_116_a_build_where_git_fails_records_the_commit_and_dirtiness_as_unknown():
    """C2: git cannot be run, or fails, at build time: the record says unknown, never a commit or dirty false."""
    head = subprocess.run(["git", "-C", str(SW.CHECKOUT), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    with tempfile.TemporaryDirectory() as t:
        ok = _provenance_target(Path(t) / "ok", {})
        failing = _provenance_target(Path(t) / "fails", {"GIT_DIR": str(Path(t) / "no-such-repository")})
        no_git = _provenance_target(Path(t) / "nogit", {"PATH": str(Path(shutil.which("dotnet")).parent)})
    assert isinstance(ok, dict) and ok.get("commit") == head and isinstance(ok.get("dirty"), bool), (
        f"with git working the target must record HEAD and a dirty flag: {ok}")
    for label, rec in (("git failing", failing), ("git absent", no_git)):
        assert isinstance(rec, dict), f"{label}: {rec}"
        assert rec.get("commit") == "unknown" and rec.get("dirty") == "unknown", (
            f"{label}: the record must say the commit and dirtiness are unknown, got {rec}")


# =============================================================== Part 2: the observations' picture comparison

W, H = 1280, 720
LIMIT = 92  # 0.01 % of 1280 x 720


def base_frame(seed: int):
    import numpy as np
    rng = np.random.default_rng(seed)
    a = rng.integers(20, 235, size=(H, W, 4), dtype=np.int16)
    a[..., 3] = 255
    return a


def args_text(run: Path, order: str = "candidate-00-baseline.json", yaw="240.2", size="1280", extra=""):
    return (f"args: 020EED:Skyrim.esm --order {run}\\prepare\\candidates\\{order} {extra}--eye -173199.0,3312.0,-3982.0 "
            f"--yaw {yaw} --pitch 25.4 --fov 65 --aspect 1.777777778 --nav off --size {size} --msaa 4 --report-mode "
            f"--no-cell-outline | pose: eye -173199,3312,-3982 yaw {yaw}° pitch 25.4° fov 65° | cell MarkarthMines")


def write_png(path: Path, arr, text: str):
    import numpy as np
    from PIL import Image
    from PIL.PngImagePlugin import PngInfo
    path.parent.mkdir(parents=True, exist_ok=True)
    info = PngInfo()
    if text is not None:
        info.add_text("viewprobe", text)
    Image.fromarray(np.clip(arr, 0, 255).astype("uint8"), "RGBA").save(path, pnginfo=info)


class Run:
    """A run folder with one captured request of three frames, observed blind and banked."""

    STATES = ("highlight", "with", "without")

    def __init__(self, t: Path):
        self.out = Path(t) / "run"
        self.sdir = self.out / "image-observations"
        self.sdir.mkdir(parents=True)
        self.seq = self.out / "capture" / "capture-a" / "sequence"
        (self.seq).mkdir(parents=True)
        (self.seq / "capture-result.json").write_text(json.dumps({"request": "capture-a", "status": "captured"}), encoding="utf-8")
        (self.out / "capture" / "captures.json").write_text(json.dumps(
            [{"id": "capture-a", "status": "captured", "result": str(self.seq / "capture-result.json")}]), encoding="utf-8")
        self.frames = {s: base_frame(i + 1) for i, s in enumerate(self.STATES)}
        self.texts = {s: args_text(self.out) for s in self.STATES}
        self.write()

    def write(self, frames=None, texts=None):
        for s in self.STATES:
            write_png(self.seq / s / "MarkarthMines-fp.png", (frames or self.frames)[s], (texts or self.texts)[s])

    def stage(self):
        try:
            return EP.stage_image_observations(types.SimpleNamespace(out=self.out), {}, self.sdir)
        except (EP.Awaiting, EP.Refusal) as ex:
            return ex

    def bank(self):
        got = self.stage()
        assert isinstance(got, EP.Awaiting), f"a first capture must wait for its observations, got {got!r}"
        key = json.loads((self.sdir / "image-key.json").read_text(encoding="utf-8"))
        obs = {"frames": [{"image": k["image"], "sha256": k["sha256"], "observation": f"frame {k['image']} described"}
                          for k in key]}
        (self.sdir / "observations.json").write_text(json.dumps(obs, indent=1), encoding="utf-8")
        self.banked = (self.sdir / "observations.json").read_bytes()
        got = self.stage()
        assert isinstance(got, tuple), f"the banked observations of the same frames were not accepted: {got!r}"
        return got


def scattered(arr, n, seed=7, level=1, rows=None):
    import numpy as np
    rng = np.random.default_rng(seed)
    out = arr.copy()
    idx = rng.choice(H * W, size=n, replace=False) if rows is None else rows
    ys, xs = np.unravel_index(idx, (H, W))
    ch = rng.integers(0, 3, size=len(ys))
    sign = rng.choice([-1, 1], size=len(ys))
    out[ys, xs, ch] += sign * level
    return out, idx


def accepted(got):
    return isinstance(got, tuple)


def asks(got):
    return isinstance(got, (EP.Awaiting, EP.Refusal))


def recapture(run: Run, change):
    """A redone capture: `change(state, frame) -> frame` for each state."""
    frames = {s: change(s, run.frames[s]) for s in run.STATES}
    run.write(frames)
    return frames


def case_201_a_recapture_within_the_tolerance_carries_the_banked_observations():
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        recapture(run, lambda s, f: scattered(f, 54, seed=len(s))[0])
        got = run.stage()
        carried = run.sdir / "carried.json"
        doc = json.loads(carried.read_text(encoding="utf-8")) if carried.is_file() else None
        banked_same = (run.sdir / "observations.json").read_bytes() == run.banked
    assert accepted(got), f"frames that differ in 54 pixels by one level asked for new descriptions: {got!r}"
    assert doc is not None, "a carried packet has no carried.json saying which frames were carried and by how much"
    assert banked_same, "the pipeline rewrote the agent's banked observations.json (F3)"


def case_202_the_count_cap_is_exact_and_one_pixel_two_levels_off_asks():
    results = {}
    for label, change in (("at the cap", lambda s, f: scattered(f, LIMIT)[0]),
                          ("one over the cap", lambda s, f: scattered(f, LIMIT + 1)[0] if s == "with" else f),
                          ("one pixel two levels", lambda s, f: scattered(f, 1, level=2)[0] if s == "with" else f)):
        with tempfile.TemporaryDirectory() as t:
            run = Run(Path(t))
            run.bank()
            recapture(run, change)
            results[label] = run.stage()
    assert accepted(results["at the cap"]), f"{LIMIT} differing pixels at one level (the cap) asked again: {results['at the cap']!r}"
    assert asks(results["one over the cap"]), f"{LIMIT + 1} differing pixels were carried"
    assert asks(results["one pixel two levels"]), "one pixel two levels off was carried"


def _asks_for(change, name):
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        recapture(run, change)
        got = run.stage()
    assert asks(got), f"{name}: the banked observations were carried to a changed picture"


def case_203_a_moved_object_asks():
    import numpy as np

    def move(s, f):
        if s != "with":
            return f
        g = f.copy()
        block = g[300:380, 500:600].copy()
        g[300:380, 500:600] = 128
        g[300:380, 540:640] = block
        return g
    _asks_for(move, "an object moved 40 px")


def case_204_a_missing_small_object_asks():
    def remove(s, f):
        if s != "with":
            return f
        g = f.copy()
        g[400:405, 700:706, :3] = 0      # 30 pixels gone to black: a far object missing
        return g
    _asks_for(remove, "a 30-pixel object missing")


def case_205_a_different_camera_asks():
    import numpy as np

    def turn(s, f):
        return np.roll(f, 3, axis=1)
    _asks_for(turn, "the camera turned by three pixels")


def case_206_a_one_level_change_over_a_region_asks():
    _asks_for(lambda s, f: f + [1, 1, 1, 0], "every pixel one level brighter")

    def patch(s, f):
        if s != "with":
            return f
        g = f.copy()
        g[100:110, 100:120, 0] += 1      # 200 contiguous pixels one level
        return g
    _asks_for(patch, "a 200-pixel region one level redder")


def case_207_a_different_size_asks():
    import numpy as np
    _asks_for(lambda s, f: f[:, :1279] if s == "with" else f, "a frame one pixel narrower")


def case_208_swapped_frames_ask():
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        f = dict(run.frames)
        f["with"], f["without"] = run.frames["without"], run.frames["with"]
        run.write(f)
        got = run.stage()
    assert asks(got), "with and without swapped were carried"


def case_209_an_added_or_removed_frame_asks():
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        (run.seq / "highlight" / "MarkarthMines-fp.png").unlink()
        got_removed = run.stage()
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        write_png(run.seq / "extra" / "MarkarthMines-fp.png", base_frame(9), args_text(run.out))
        got_added = run.stage()
    assert asks(got_removed), "a removed frame was carried"
    assert asks(got_added), "an added frame was carried"


def case_210_described_frames_that_are_gone_or_changed_ask():
    def tamper(run, how):
        for d in (run.sdir / "described", run.sdir / "indexed-images"):
            p = d / "image-002.png"
            if p.is_file():
                if how == "gone":
                    p.unlink()
                else:
                    write_png(p, scattered(run.frames["with"], 3)[0], run.texts["with"])
    results = {}
    for how in ("gone", "changed"):
        with tempfile.TemporaryDirectory() as t:
            run = Run(Path(t))
            run.bank()
            tamper(run, how)
            recapture(run, lambda s, f: scattered(f, 10)[0])
            results[how] = run.stage()
    assert asks(results["gone"]), "the described frame is gone, yet its observation was carried"
    assert asks(results["changed"]), "the described frame no longer hashes to the banked sha256, yet its observation was carried"


def case_211_small_steps_do_not_drift_past_the_described_frames():
    import numpy as np
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        step1 = {s: scattered(run.frames[s], 60, seed=1)[0] for s in run.STATES}
        run.write(step1)
        first = run.stage()
        step2 = {s: scattered(step1[s], 60, seed=2)[0] for s in run.STATES}   # within 92 of step 1, 120 from the described
        run.write(step2)
        second = run.stage()
    assert accepted(first), f"precondition: a 60-pixel recapture must be carried, got {first!r}"
    assert asks(second), "a second recapture 60 pixels from the first and about 120 from the described frames was carried"


def case_212_a_nudged_camera_asks_even_when_its_pixels_are_within_tolerance():
    """F3: the recorded render arguments (PNG tEXt 'viewprobe') are compared as data."""
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        texts = dict(run.texts)
        texts["with"] = args_text(run.out, yaw="240.3")
        run.write({s: scattered(run.frames[s], 10)[0] for s in run.STATES}, texts)
        got = run.stage()
    assert asks(got), "the camera's yaw changed from 240.2 to 240.3 and the observation was carried on pixels alone"


def case_213_a_moved_run_folder_still_carries():
    """The order file's folder depends on where the run folder sits; its name (and, upstream, its bytes) is what counts."""
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        texts = {s: args_text(Path(t) / "elsewhere" / "run") for s in run.STATES}
        run.write({s: scattered(run.frames[s], 10)[0] for s in run.STATES}, texts)
        got = run.stage()
    assert accepted(got), f"a recapture in a moved run folder (order path only differs by folder) asked again: {got!r}"


def case_217_another_path_argument_that_differs_asks():
    """C1: only the --order argument's folder is set aside; any other path-valued argument is compared exactly."""
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.texts = {s: args_text(run.out, extra="--mask-from C:\\data\\a\\mask.png ") for s in run.STATES}
        run.write()
        run.bank()
        texts = {s: args_text(run.out, extra="--mask-from C:\\data\\b\\mask.png ") for s in run.STATES}
        run.write({s: scattered(run.frames[s], 10)[0] for s in run.STATES}, texts)
        got = run.stage()
    assert asks(got), "a path argument other than --order named another folder and the observation was carried"


def case_218_frames_with_no_recorded_arguments_ask():
    """Review of df33d8f4: a frame whose render arguments were not recorded (no 'viewprobe' text) is never the same
    picture, even when neither side has them (absent == absent is not "the same arguments")."""
    results = {}
    for label, before, after in (("neither side", None, None), ("the redone side", "keep", None)):
        with tempfile.TemporaryDirectory() as t:
            run = Run(Path(t))
            if before is None:
                run.texts = {s: None for s in run.STATES}
                run.write()
            run.bank()
            texts = {s: (run.texts[s] if after == "keep" else None) for s in run.STATES}
            run.write({s: scattered(run.frames[s], 10)[0] for s in run.STATES}, texts)
            results[label] = run.stage()
    assert asks(results["neither side"]), "frames with no recorded render arguments on either side were carried on pixels alone"
    assert asks(results["the redone side"]), "a redone frame with no recorded render arguments was carried"


def case_214_a_hand_edited_carry_record_asks():
    """F3: carried.json is the pipeline's record of the carry; an edited one is not trusted."""
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        recapture(run, lambda s, f: scattered(f, 20)[0])
        first = run.stage()
        carried = run.sdir / "carried.json"
        assert accepted(first) and carried.is_file(), f"precondition: a 20-pixel recapture is carried with a record, got {first!r}"
        doc = json.loads(carried.read_text(encoding="utf-8"))
        carried.write_text(json.dumps(doc).replace('"maxLevel": 1', '"maxLevel": 0'), encoding="utf-8")
        edited = run.stage()
    assert asks(edited), "a hand-edited carried.json was accepted"


def case_215_a_removed_carry_record_is_made_again_from_the_evidence():
    """The carry is decided from the described frames and the new frames; a removed record is recomputed, identical."""
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        recapture(run, lambda s, f: scattered(f, 20)[0])
        first = run.stage()
        carried = run.sdir / "carried.json"
        assert accepted(first) and carried.is_file(), f"precondition: a 20-pixel recapture is carried with a record, got {first!r}"
        before = carried.read_bytes()
        carried.unlink()
        again = run.stage()
        after = carried.read_bytes() if carried.is_file() else None
    assert accepted(again) and after == before, "a removed carried.json was not made again, byte for byte, from the described frames"


def case_216_the_carry_record_is_bound_in_the_stage_receipt():
    with tempfile.TemporaryDirectory() as t:
        run = Run(Path(t))
        run.bank()
        recapture(run, lambda s, f: scattered(f, 20)[0])
        got = run.stage()
    assert accepted(got), f"precondition: a 20-pixel recapture is carried, got {got!r}"
    outputs = [Path(p).name for p in got[1]]
    assert "carried.json" in outputs, f"carried.json is not among the stage's bound outputs: {outputs}"


def main():
    for name, fn in sorted(((k, v) for k, v in globals().items() if k.startswith("case_")),
                           key=lambda kv: int(kv[0].split("_")[1])):
        check(name, fn)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
