"""Known answers for the evaluation's first-run settings and window (evalsettings.py), PROPOSAL section 7.

    python _geometry-harness/tests/test_evalsettings.py

Each test uses its own empty HOUSECARL_EVAL_SETTINGS location and the stub opener
(tests/fixtures/evalsettings/stub_opener.py, passed with --opener) as the browser, so no real browser opens and no
test reads the user's own settings file. 1 a fresh user gets the window once; 2 a saved choice is honoured by
`evalrun plan`; 3 flag > environment > file > built-in default; 4 a missing file with --no-window; 5 refusals of a bad
file; 6 closing without Save; 7 the server is loopback-only and checks the token and the body; 8 a later save does
not change a frozen plan. Light: fake probes only, the state checks only.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
HARNESS = HERE.parent
EVALSETTINGS = HARNESS / "evalsettings.py"
STUB = HERE / "fixtures" / "evalsettings" / "stub_opener.py"
NAME = "houseCARL.evaluation.json"
LIVE = "runtime.state-switch-flip"

sys.path.insert(0, str(HERE))
import test_stategen as S  # noqa: E402  (the controller fixture driver and census builder)

FAILURES, PASSED = [], []


def check(cond, label):
    (PASSED if cond else FAILURES).append(label)
    print(("ok    " if cond else "FAIL  ") + label)


def guarded(label, fn):
    try:
        fn()
    except Exception as ex:  # noqa: BLE001
        check(False, f"{label}: raised {type(ex).__name__}: {ex}")
        traceback.print_exc(limit=2)


def sha(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


class Box:
    """One test's private settings location, stub-opener log and environment."""

    def __init__(self, root: Path, name: str):
        self.dir = root / name
        self.dir.mkdir(parents=True)
        self.file = self.dir / "settings" / NAME
        self.log = self.dir / "opener.jsonl"
        self.env = dict(os.environ)
        self.env.pop("HOUSECARL_EVAL_UNATTENDED", None)
        self.env.pop("HOUSECARL_FFMPEG", None)
        self.env["HOUSECARL_EVAL_SETTINGS"] = str(self.file)
        self.env["STUB_OPENER_LOG"] = str(self.log)

    def es(self, *args, mode="save", extra_env=None, timeout=120):
        env = dict(self.env)
        env["STUB_OPENER_MODE"] = mode
        env.update(extra_env or {})
        return subprocess.run([sys.executable, str(EVALSETTINGS)] + [str(a) for a in args], capture_output=True,
                              text=True, env=env, timeout=timeout)

    def ensure(self, *args, mode="save", timeout_s=20):
        return self.es("ensure", "--opener", STUB, "--timeout-seconds", timeout_s, *args, mode=mode)

    def calls(self):
        if not self.log.exists():
            return []
        return [json.loads(x) for x in self.log.read_text(encoding="utf-8").splitlines() if x.strip()]


def t1(root):
    b = Box(root, "t1")
    r = b.ensure()
    check(r.returncode == 0, f"1: ensure with no file exits 0 after Save ({(r.stdout + r.stderr)[-300:]!r})")
    check(len(b.calls()) == 1 and b.calls()[0].get("save") == 200, "1: the window opened once and Save was accepted")
    check(b.file.exists(), "1: the settings file appears at the resolved location")
    v = b.es("validate", b.file)
    check(v.returncode == 0, "1: the saved file validates")
    r2 = b.ensure()
    lines = [x for x in r2.stdout.splitlines() if x.strip()]
    check(r2.returncode == 0 and len(b.calls()) == 1, "1: a second ensure does not open the window")
    check(len(lines) == 1 and str(b.file) in lines[0], f"1: it prints one line naming the file ({lines!r})")


def t2(root):
    b = Box(root, "t2")
    ctl = S.Ctl(b.dir / "fx")
    scen = ctl.scenario(S.census_g1(), S.census_g1())
    paths = [f"liveSwitchCheck.instance={b.dir / 'scratch'}", f"liveSwitchCheck.shortcut={b.dir / 'game.lnk'}",
             f"liveSwitchCheck.gameExe={b.dir / 'game.exe'}"]
    r = b.es("set", "evaluation.liveSwitchCheck=always", "evaluation.reverseComparison=false", *paths)
    check(r.returncode == 0 and b.file.exists(), f"2: set writes the choices ({(r.stdout + r.stderr)[-300:]!r})")
    run = b.dir / "plan-with-file"
    p = ctl.plan(run, scen, registry="fixture-check-registry-live.json",
                 env_extra={"HOUSECARL_EVAL_SETTINGS": str(b.file)})
    if p.returncode:
        check(False, f"2: evalrun plan reads the file ({(p.stdout + p.stderr)[-300:]!r})")
    else:
        pl = load(run / "plan.json")
        row = next((c for c in pl["checks"] if c["checkId"] == LIVE), None)
        check(row is not None and row["required"] is True
              and {a["status"] for a in row["arms"]} <= {"live-import", "not-applicable"},
              "2: liveSwitchCheck always opts the plan into the live check without --opt-in")
        check(pl["options"].get("noReverse") is True, "2: reverseComparison false freezes noReverse")
        st = (pl.get("explicitDecisions") or {}).get("settings") or {}
        check(st.get("sha256") == sha(b.file) and st.get("schemaVersion") == 1,
              "2: explicitDecisions.settings carries the file's hash and schema")
    absent = b.dir / "plan-no-file"
    p0 = ctl.plan(absent, scen, registry="fixture-check-registry-live.json",
                  env_extra={"HOUSECARL_EVAL_SETTINGS": str(b.dir / "none" / NAME)})
    if p0.returncode == 0:
        row0 = next((c for c in load(absent / "plan.json")["checks"] if c["checkId"] == LIVE), None)
        check(row0 is not None and {a["status"] for a in row0["arms"]} == {"not-opted-in"},
              "2 (red arm): with the file absent the same plan shows the live check not opted in")
    else:
        check(False, f"2 (red arm): plan without the file ({(p0.stdout + p0.stderr)[-300:]!r})")
    b.es("set", "evaluation.liveSwitchCheck=off", "evaluation.depth=standard")
    std = b.dir / "plan-standard"
    ps = ctl.plan(std, scen, env_extra={"HOUSECARL_EVAL_SETTINGS": str(b.file)})
    check(ps.returncode == 0 and (load(std / "plan.json").get("states") or {}).get("depth") == "standard",
          "2: a saved depth of standard is honoured")


def t3(root):
    b = Box(root, "t3")
    b.es("set", "report.mode=exhaustive", "video.ffmpeg=C:/from-file/ffmpeg.exe")
    r = b.es("show", "--json", extra_env={"HOUSECARL_FFMPEG": "C:/from-env/ffmpeg.exe"})
    try:
        opts = json.loads(r.stdout)["options"]
    except Exception:  # noqa: BLE001
        check(False, f"3: show --json prints the options ({(r.stdout + r.stderr)[-300:]!r})")
        return
    check(opts["report.mode"] == {"value": "exhaustive", "source": "file"}, "3: the file beats the default")
    check(opts["video.ffmpeg"] == {"value": "C:/from-env/ffmpeg.exe", "source": "environment"},
          "3: an existing environment variable beats the file")
    check(opts["evaluation.depth"] == {"value": "deep", "source": "built-in default"},
          "3: an unset option is the built-in default (depth deep)")
    ctl = S.Ctl(b.dir / "fx")
    scen = ctl.scenario(S.census_g1(), S.census_g1())
    b.es("set", "evaluation.depth=standard")
    run = b.dir / "flag"
    p = ctl.plan(run, scen, ["--depth", "deep"], env_extra={"HOUSECARL_EVAL_SETTINGS": str(b.file)})
    o = ((((load(run / "plan.json").get("explicitDecisions") or {}).get("settings") or {}).get("options") or {})
         if p.returncode == 0 else {})
    check(o.get("evaluation.depth") == {"value": "deep", "source": "flag"}, "3: a flag beats the file in the plan")
    env = dict(b.env)
    env.pop("HOUSECARL_EVAL_SETTINGS")
    env["HOUSECARL_DATA_DIR"] = str(b.dir / "data")
    pth = subprocess.run([sys.executable, str(EVALSETTINGS), "path"], capture_output=True, text=True, env=env)
    check(pth.returncode == 0 and Path(pth.stdout.strip()) == b.dir / "data" / NAME,
          f"3: without HOUSECARL_EVAL_SETTINGS the file is under HOUSECARL_DATA_DIR ({pth.stdout.strip()!r})")


def t4(root):
    b = Box(root, "t4")
    r = b.ensure("--no-window")
    check(r.returncode == 0 and not b.calls() and not b.file.exists(),
          "4: --no-window opens nothing and writes nothing")
    check("No saved evaluation settings were found" in (r.stdout + r.stderr),
          "4: it says the built-in defaults are used")
    u = b.es("ensure", "--opener", STUB, extra_env={"HOUSECARL_EVAL_UNATTENDED": "1"})
    check(u.returncode == 0 and not b.calls(), "4: HOUSECARL_EVAL_UNATTENDED=1 opens nothing either")
    ctl = S.Ctl(b.dir / "fx")
    scen = ctl.scenario(S.census_g1(), S.census_g1())
    run = b.dir / "plan"
    p = ctl.plan(run, scen, env_extra={"HOUSECARL_EVAL_SETTINGS": str(b.file)})
    st = ((load(run / "plan.json").get("explicitDecisions") or {}).get("settings") or {}) if p.returncode == 0 else {}
    check(st.get("source") == "built-in defaults", "4: the plan records source: built-in defaults")


def t5(root):
    b = Box(root, "t5")
    b.file.parent.mkdir(parents=True)
    bad = {
        "invalid JSON": ("{ not json", "not valid JSON"),
        "unknown key": (json.dumps({"schemaVersion": 1, "evaluation": {"colour": "red"}}), "evaluation.colour"),
        "bad value": (json.dumps({"schemaVersion": 1, "video": {"make": "sometimes"}}), "not one of"),
        "newer schema": (json.dumps({"schemaVersion": 99}), "newer houseCARL"),
    }
    for name, (text, phrase) in bad.items():
        b.file.write_text(text, encoding="utf-8")
        before = sha(b.file)
        for cmd in (("validate", b.file), ("ensure", "--no-window"), ("show",)):
            r = b.es(*cmd)
            out = r.stdout + r.stderr
            check(r.returncode == 2 and str(b.file) in out and phrase in out,
                  f"5 {name}: {cmd[0]} exits 2 naming the file and the problem")
        check(sha(b.file) == before, f"5 {name}: the file is unchanged")
        check(not b.calls(), f"5 {name}: no window opened over a bad file")


def t6(root):
    b = Box(root, "t6")
    r = b.ensure(mode="close")
    check(r.returncode == 0 and not b.file.exists(), "6: closing without Save writes nothing")
    check("No saved evaluation settings were found" in (r.stdout + r.stderr),
          "6: the evaluation goes on with the built-in defaults and says so")
    b.ensure(mode="close")
    check(len(b.calls()) == 2, "6: the next ensure opens the window again")
    t = b.ensure(mode="none", timeout_s=3)
    check(t.returncode == 0 and not b.file.exists() and len(b.calls()) == 3,
          "6: a forgotten tab: the server stops on its operational timeout and writes nothing")


def t7(root):
    b = Box(root, "t7")
    b.ensure(mode="probe")
    c = (b.calls() or [{}])[0]
    check(c.get("host") == "127.0.0.1", f"7: the page is served from 127.0.0.1 ({c.get('host')!r})")
    check(c.get("noToken") == 403, f"7: a POST without the token is refused ({c.get('noToken')})")
    check(c.get("badBody") == 400, f"7: a POST with an invalid body is refused ({c.get('badBody')})")
    probed = c.get("block") is True
    check(probed and (c.get("otherAddress") is None or c.get("otherConnect") not in (0, None)),
          f"7: the port does not answer on a non-loopback address ({c.get('otherAddress')}: {c.get('otherConnect')})")
    check(probed and not b.file.exists(), "7: neither refused POST wrote the file")


def t8(root):
    b = Box(root, "t8")
    b.es("set", "evaluation.depth=standard")
    ctl = S.Ctl(b.dir / "fx")
    scen = ctl.scenario(S.census_g1(), S.census_g1())
    run = b.dir / "run"
    env = {"HOUSECARL_EVAL_SETTINGS": str(b.file)}
    p = ctl.plan(run, scen, env_extra=env)
    if p.returncode:
        check(False, f"8: plan written ({(p.stdout + p.stderr)[-300:]!r})")
        return
    plan_sha = sha(run / "plan.json")
    frozen = (load(run / "plan.json").get("states") or {}).get("depth")
    check(frozen == "standard", f"8: the plan froze the file's Standard depth ({frozen!r})")
    b.es("set", "evaluation.depth=deep")
    r = ctl.ev(["run", "--run", run], scen, env)
    row = S.ledger_status(run, S.CG, S.BASE_ARM) or {}
    check(frozen == "standard" and r.returncode != 2 and row.get("status") == "skipped-user",
          "8: run follows the plan's frozen Standard depth, not the file's later Deep")
    b.file.write_text("{ broken", encoding="utf-8")
    rr = ctl.ev(["resume", "--run", run], scen, env)
    check(rr.returncode != 2, f"8: resume never reads the settings file ({(rr.stdout + rr.stderr)[-200:]!r})")
    check(sha(run / "plan.json") == plan_sha, "8: the plan is unchanged")


def main() -> int:
    root = Path(tempfile.mkdtemp(prefix="evalsettings-"))
    os.environ["HOUSECARL_EVAL_SETTINGS"] = str(root / "no-settings" / NAME)
    try:
        for label, fn in (("1", t1), ("2", t2), ("3", t3), ("4", t4), ("5", t5), ("6", t6), ("7", t7), ("8", t8)):
            guarded(label, lambda fn=fn: fn(root))
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
