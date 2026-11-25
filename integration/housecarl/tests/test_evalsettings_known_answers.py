"""evalsettings.load against the answers recorded from it, moved from runcontrol's self-test when the plan stopped reading
the evaluation settings (the C# reader was a port of this file; it is gone, and this file is the one owner):

    python integration/housecarl/tests/test_evalsettings_known_answers.py

    settings: record, path, values and sentences of N loads
    settings: the sentence of each of N refusals
    settings: where Python itself raises
    settings: the cases cover the four values plan reads, each source and each location
    settings: the accepted model IDs of an agents folder
and the three plan-command rows about the settings file that now belong here:
    plan command: settings: the live check asks, and nothing opted in      (the module says so; nothing is folded)
    plan command: settings: the live check asks, and the flag opts in      (an entry that opts in is left as it is)
    plan command: settings: a file that is not valid is refused            (the module refuses the run in that sentence)

Fixture: fixtures/evalsettings/known-answers.json (each case's files, environment, explicit path and flags, and its
answer, with the case's folder masked).
"""
from __future__ import annotations

import base64
import json
import os
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))  # the module's folder
import evalpipeline as EP  # noqa: E402
import evalsettings as ES  # noqa: E402

KA = json.loads((HERE / "fixtures" / "evalsettings" / "known-answers.json").read_text(encoding="utf-8"))["settings"]
ENV_KEYS = ("HOUSECARL_EVAL_SETTINGS", "HOUSECARL_DATA_DIR", "LOCALAPPDATA", "XDG_STATE_HOME", "HOUSECARL_FFMPEG")
PASSED, FAILURES = [], []


def check(name, why):
    print(f"  {'PASS' if why is None else 'FAIL'}  {name}" + ("" if why is None else f": {why}"))
    (PASSED if why is None else FAILURES).append(name)


class Case:
    """One case's folder: its files written, its environment set (the rest of the settings variables cleared)."""

    def __init__(self, row):
        self.t = tempfile.TemporaryDirectory(prefix="hc-settings-ka-")
        self.root = str(Path(self.t.name).resolve())
        for rel, b64 in (row.get("files") or {}).items():
            p = Path(self.root) / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(base64.b64decode(b64))
        self.saved = {k: os.environ.get(k) for k in ENV_KEYS}
        for k in ENV_KEYS:
            os.environ.pop(k, None)
        for k, v in (row.get("env") or {}).items():
            os.environ[k] = self.unmask(v)

    def unmask(self, s):
        return s.replace("<ROOT\\>", self.root).replace("<ROOT/>", self.root.replace("\\", "/"))

    def mask(self, s):
        return s.replace(self.root, "<ROOT\\>").replace(self.root.replace("\\", "/"), "<ROOT/>")

    def mask_all(self, v):
        if isinstance(v, str):
            return self.mask(v)
        if isinstance(v, list):
            return [self.mask_all(x) for x in v]
        if isinstance(v, dict):
            return {self.mask(k): self.mask_all(x) for k, x in v.items()}
        return v

    def close(self):
        for k, v in self.saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
        self.t.cleanup()


def load(row):
    c = Case(row)
    try:
        explicit = c.unmask(row["explicit"]) if isinstance(row.get("explicit"), str) else None
        try:
            return c, ES.load(explicit, row.get("flags")), None, None
        except ES.SettingsError as ex:
            return c, None, c.mask(str(ex)), None
        except Exception as ex:  # noqa: BLE001  where Python itself raises is a recorded answer too
            return c, None, None, str(ex)
    except BaseException:
        c.close()
        raise


def compact(v):
    return json.dumps(v, ensure_ascii=False, separators=(",", ":"))


def loads_case():
    rows = [r for r in KA["cases"] if "record" in r]
    for row in rows:
        c, s, error, raised = load(row)
        try:
            if s is None:
                return f"{row['name']}: refused: {error or raised}"
            rec = c.mask_all(s.record())
            got = {k: s.value(k) for k in row["values"]}
            for what, a, b in (("record", compact(rec), row["record"]), ("path", c.mask(str(s.path)), row["path"]),
                               ("values", json.dumps(got), json.dumps(row["values"])), ("sentences", s.sentences(), row["sentences"])):
                if a != b:
                    return f"{row['name']}: {what}: got {str(a)[:300]}, recorded {str(b)[:300]}"
        finally:
            c.close()
    return None


def refusals_case():
    for row in [r for r in KA["cases"] if "error" in r]:
        c, s, error, raised = load(row)
        c.close()
        got = error if error is not None else ("raised: " + raised if raised is not None else None)
        if s is not None:
            return f"{row['name']}: nothing was refused"
        if got != row["error"]:
            return f"{row['name']}: got {got!r}, recorded {row['error']!r}"
    return None


def raises_case():
    for row in [r for r in KA["cases"] if "raises" in r]:
        c, s, error, raised = load(row)
        c.close()
        got = raised if raised is not None else ("loaded" if s is not None else "refused: " + error)
        if got != row["raises"]:
            return f"{row['name']}: got {got!r}, recorded {row['raises']!r}"
    return None


def cover_case():
    loaded = [r for r in KA["cases"] if "record" in r]
    every = "".join(r["record"] for r in loaded)
    for source in ("flag", "environment", "file", "built-in default"):
        if f'"source":"{source}"' not in every:
            return f"no case takes a value from: {source}"
    for key in ("HOUSECARL_EVAL_SETTINGS", "HOUSECARL_DATA_DIR", "LOCALAPPDATA"):
        if not any(key in (r.get("env") or {}) and r.get("explicit") is None for r in KA["cases"]):
            return f"no case finds the file through {key}"
    seen = len({json.dumps(r["values"]) for r in loaded})
    return None if seen >= 12 else f"only {seen} distinct sets of the four values"


def agents_case():
    with tempfile.TemporaryDirectory() as t:
        folder = Path(t) / "agents"
        folder.mkdir()
        for name, text in KA["agents"]["files"].items():
            (folder / name).write_bytes(text.encode("utf-8"))
        ids = list(ES._model_agents(folder))
        missing = list(ES._model_agents(Path(t) / "none"))
    if ids != KA["agents"]["ids"]:
        return f"ids {ids}, recorded {KA['agents']['ids']}"
    return None if missing == KA["agents"]["missingFolder"] else f"a missing folder gives {missing}"


ASK = '{"schemaVersion": 1, "saved": {"utc": "2026-11-01T00:00:00Z", "by": "window", "houseCARL": "2.0.0"}, "evaluation": {"liveSwitchCheck": "ask"}}'
ENTRY = {"schema": 1, "id": "m", "source": {"instance": "S:/src", "profile": "P"}, "evaluation": {"depth": "deep"}}


def settings_at(text):
    t = tempfile.mkdtemp(prefix="hc-settings-row-")
    p = Path(t) / "houseCARL.evaluation.json"
    p.write_text(text, encoding="utf-8")
    return p


def asks_nothing_opted():
    folded, said = EP.measuring_entry(json.loads(json.dumps(ENTRY)), ES.load(str(settings_at(ASK))))
    return None if folded == ENTRY and len(said) == 1 and "set to ask each evaluation and was not opted in" in said[0] \
        and "--opt-in runtime.state-switch-flip" in said[0] else f"folded {folded}, said {said}"


def asks_flag_opts_in():
    entry = dict(json.loads(json.dumps(ENTRY)), evaluation={"depth": "deep", "optIn": ["runtime.state-switch-flip"]})
    folded, said = EP.measuring_entry(json.loads(json.dumps(entry)), ES.load(str(settings_at(ASK))))
    return None if folded == entry and said == [] else f"folded {folded}, said {said}"


def invalid_file():
    p = settings_at('{"schemaVersion": 1, "evaluation": {"depth": "shallow"}}')
    try:
        ES.load(str(p))
    except ES.SettingsError as ex:
        want = f"The evaluation settings file `{p}` sets `evaluation.depth` to `shallow`, which is not one of standard, deep: fix that value or reopen the window."
        return None if str(ex) == want else f"said {ex!s}"
    return "the file was accepted"


def main() -> int:
    loaded = sum(1 for r in KA["cases"] if "record" in r)
    refused = sum(1 for r in KA["cases"] if "error" in r)
    check(f"settings: record, path, values and sentences of {loaded} loads", loads_case())
    check(f"settings: the sentence of each of {refused} refusals", refusals_case())
    check("settings: where Python itself raises", raises_case())
    check("settings: the cases cover the four values plan reads, each source and each location", cover_case())
    check("settings: the accepted model IDs of an agents folder", agents_case())
    check("plan command: settings: the live check asks, and nothing opted in", asks_nothing_opted())
    check("plan command: settings: the live check asks, and the flag opts in", asks_flag_opts_in())
    check("plan command: settings: a file that is not valid is refused", invalid_file())
    print(f"\n{len(PASSED)} passed, {len(FAILURES)} failed")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
