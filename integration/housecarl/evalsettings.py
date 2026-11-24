"""evalsettings — the first-run choices for the worldspace compatibility evaluation, and the window that sets them.

    python evalsettings.py path
    python evalsettings.py show [--json]
    python evalsettings.py ensure [--no-window] [--opener SCRIPT] [--timeout-seconds N]
    python evalsettings.py window [--opener SCRIPT] [--timeout-seconds N] [--page-only FILE]
    python evalsettings.py set KEY=VALUE [KEY=VALUE ...]
    python evalsettings.py defaults --out FILE
    python evalsettings.py validate FILE
    python evalsettings.py route KEY

Every command takes `--settings FILE`. The file is `houseCARL.evaluation.json`, found by `--settings`, then
HOUSECARL_EVAL_SETTINGS, then HOUSECARL_DATA_DIR, then %LOCALAPPDATA%/houseCARL (elsewhere XDG_STATE_HOME/housecarl or
~/.local/state/housecarl): the run log's own chain, so the two land side by side. A key that is absent or null takes
the built-in default, so a later improvement to a default reaches a user who never chose.

`ensure` is the evaluation's first step: with a file it prints one line naming it; with none it opens the window
(a one-shot server on 127.0.0.1, a random token in the page's address, one accepted POST, an operational timeout) and
waits for Save or close. `--no-window` or HOUSECARL_EVAL_UNATTENDED=1 never opens it: a missing file then means the
built-in defaults, said in one sentence. `--opener SCRIPT` runs `python SCRIPT URL` instead of the browser (tests).
A bad file is refused (exit 2) and never replaced: a silent fall-back could drop a choice such as "never launch the game".

Precedence for each option: a command-line flag (the evaluation tool's own) > an environment variable where one already
exists (HOUSECARL_FFMPEG) > the file > the built-in default. `show --json` prints each value with its source.

A model choice (`models.*`) is `session`, `off` where allowed, or a full model ID that has an agent in the plugin's
`agents/` folder; that folder is the one list of accepted IDs. A short name such as `haiku` is refused: the host
resolves it to different models. `route KEY` prints the agent to start (Claude Code only; elsewhere it refuses).
"""
from __future__ import annotations

import argparse
import hashlib
import html
import json
import os
import secrets
import subprocess
import sys
import tempfile
import threading
import time
import webbrowser
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import skywright_paths as SW  # noqa: E402  where SkyWright's checkout is: its tools and run log on sys.path
from runlog import Run  # noqa: E402

SCHEMA_VERSION = 1
FILE_NAME = "houseCARL.evaluation.json"
TEMPLATE = HERE / "evalsettings_window.html"
DEFAULT_TIMEOUT_S = 3600.0
EXIT_OK, EXIT_REFUSED = 0, 2
PLUGIN_DIR = SW.CHECKOUT / "external" / "houseCARL" / "plugin"


def _model_agents(folder: Path) -> dict[str, str]:
    """{full model ID: agent name} from the shipped agent definitions' frontmatter."""
    out = {}
    for f in sorted(folder.glob("*.md")) if folder.is_dir() else []:
        lines = f.read_text(encoding="utf-8").splitlines()
        if not lines or lines[0].strip() != "---":
            continue
        head = {}
        for line in lines[1:]:
            if line.strip() == "---":
                break
            k, sep, v = line.partition(":")
            if sep:
                head[k.strip()] = v.strip()
        if head.get("model") and head.get("name"):
            out[head["model"]] = head["name"]
    return out


def _plugin_name() -> str:
    """The plugin's name, which the host puts before an agent's name (`housecarl:model-...`)."""
    return str(json.loads((PLUGIN_DIR / ".claude-plugin" / "plugin.json").read_text(encoding="utf-8"))["name"])


MODEL_AGENTS = _model_agents(PLUGIN_DIR / "agents")

# The option table: the window, validation, `show` and `defaults` all read this one list. `default` None means the
# evaluation tool's own default (named in `defaultText`); `when` shows the option only under another option's value.
OPTIONS: list[dict] = [
    # -- the evaluation -----------------------------------------------------------------------------------------
    {"key": "evaluation.depth", "type": "enum", "allowed": ["standard", "deep"], "default": "deep",
     "section": "Evaluation", "label": "Evaluation depth",
     "help": "Standard judges the load-time state only. Deep also judges every switched state the switch census finds "
             "(each enable marker flipped alone, and each quest stage's joint flip), offline, and allows the live check.",
     "reason": "Deep: a mod that hangs content on a marker another mod flips clashes only in the flipped state.",
     "cost": "Deep adds a stateprobe run per arm over every switched state; no game launch, no model usage."},
    {"key": "evaluation.liveSwitchCheck", "type": "enum", "allowed": ["off", "ask", "always"], "default": "off",
     "section": "Evaluation", "label": "Live switch check (starts the game)",
     "help": "Start the game on a scratch instance and flip the switches the mods' quest scripts flip, watching for a "
             "crash or a script stall. Always makes it required: the evaluation cannot finish without game launches.",
     "reason": "Off: it launches the game and needs a scratch MO2 instance, DevBench, a crash logger and recording.",
     "cost": "Game launches; at least two per arm and condition.", "when": {"evaluation.depth": ["deep"]}},
    {"key": "evaluation.reverseComparison", "type": "bool", "default": True, "section": "Evaluation", "more": True,
     "label": "Reverse comparison",
     "help": "Also re-measure the starting setup with the candidate as the base, as a cross-check.",
     "reason": "On: its rows go to the evidence index only, so it costs machine time, not reader attention.",
     "cost": "One more sweep part per candidate."},
    {"key": "evaluation.budgetMinutes", "type": "number", "default": None, "section": "Evaluation", "more": True,
     "label": "Time limit per run (minutes)", "defaultText": "none",
     "help": "Stop launching new checks after this long; a check still running is recorded stopped, never completed.",
     "reason": "None: a first evaluation should finish; a deadline produces an incomplete one.", "cost": "-"},
    {"key": "evaluation.nexusLookups", "type": "bool", "default": True, "section": "Evaluation",
     "label": "Look on Nexus for patches and the authors' notes",
     "help": "Keyless, read-only lookups of mod pages; the stored copy is used when Nexus does not answer.",
     "reason": "On: patch discovery is a large part of what a modder wants.", "cost": "Network requests to Nexus."},
    # -- the live check's machine -------------------------------------------------------------------------------
    {"key": "liveSwitchCheck.instance", "type": "path", "default": None, "section": "Live switch check",
     "label": "Scratch MO2 instance folder", "defaultText": "not set",
     "help": "The instance the game is launched from. Never your own play instance.",
     "reason": "None: required when the live check is on.", "cost": "-",
     "when": {"evaluation.liveSwitchCheck": ["ask", "always"]}},
    {"key": "liveSwitchCheck.shortcut", "type": "path", "default": None, "section": "Live switch check",
     "label": "That instance's launch shortcut", "defaultText": "not set", "help": "How the game is started.",
     "reason": "None: required when the live check is on.", "cost": "-",
     "when": {"evaluation.liveSwitchCheck": ["ask", "always"]}},
    {"key": "liveSwitchCheck.gameExe", "type": "path", "default": None, "section": "Live switch check",
     "label": "Game executable", "defaultText": "not set", "help": "The game the shortcut starts.",
     "reason": "None: required when the live check is on.", "cost": "-",
     "when": {"evaluation.liveSwitchCheck": ["ask", "always"]}},
    {"key": "liveSwitchCheck.crashDir", "type": "path", "default": None, "section": "Live switch check",
     "label": "Crash-log folder", "defaultText": "~/Documents/My Games/Skyrim Special Edition/SKSE",
     "help": "Where crash logs are looked for.", "reason": "The live driver's default.", "cost": "-",
     "when": {"evaluation.liveSwitchCheck": ["ask", "always"]}},
    {"key": "liveSwitchCheck.devbenchPort", "type": "int", "default": None, "section": "Live switch check",
     "more": True, "label": "DevBench port", "defaultText": "8920",
     "help": "The local port the in-game control server listens on.", "reason": "The live driver's default.",
     "cost": "-", "when": {"evaluation.liveSwitchCheck": ["ask", "always"]}},
    {"key": "liveSwitchCheck.minFreeGb", "type": "number", "default": None, "section": "Live switch check",
     "more": True, "label": "Free memory needed before a launch (GB)", "defaultText": "16",
     "help": "The idle gate refuses a launch below this.", "reason": "The live driver's default.", "cost": "-",
     "when": {"evaluation.liveSwitchCheck": ["ask", "always"]}},
    {"key": "liveSwitchCheck.launchMinutes", "type": "number", "default": None, "section": "Live switch check",
     "more": True, "label": "The most one launch may take (minutes)", "defaultText": "25",
     "help": "A launch still running at it is stopped and recorded as not measured (an operational timeout).",
     "reason": "The live driver's default.", "cost": "-",
     "when": {"evaluation.liveSwitchCheck": ["ask", "always"]}},
    # -- the report ---------------------------------------------------------------------------------------------
    {"key": "report.mode", "type": "enum", "allowed": ["concise", "exhaustive"], "default": "concise",
     "section": "Report", "label": "Report length",
     "help": "Concise answers the reader's question on the first pages; exhaustive lists every row with no page limit.",
     "reason": "Concise: the evidence index keeps every row either way.", "cost": "-"},
    {"key": "report.pdf", "type": "enum", "allowed": ["when-browser-found", "off"], "default": "when-browser-found",
     "section": "Report", "label": "PDF copy",
     "help": "A Chromium browser prints the HTML report; without one the report is Markdown and HTML only.",
     "reason": "On when a browser is found: it costs nothing extra.", "cost": "-"},
    {"key": "report.maxPages", "type": "int", "default": None, "section": "Report", "more": True,
     "label": "Page target (concise)", "defaultText": "the skill's value",
     "help": "Concise reports abridge to fit.", "reason": "The skill's value.", "cost": "-"},
    {"key": "report.rowsPerGroup", "type": "int", "default": None, "section": "Report", "more": True,
     "label": "Rows per group", "defaultText": "the skill's value", "help": "How many rows a finding shows.",
     "reason": "The skill's value.", "cost": "-"},
    {"key": "report.figuresPerFinding", "type": "int", "default": None, "section": "Report", "more": True,
     "label": "Figures per finding", "defaultText": "the skill's value", "help": "How many figures a finding shows.",
     "reason": "The skill's value.", "cost": "-"},
    # -- the video ----------------------------------------------------------------------------------------------
    {"key": "video.make", "type": "enum", "allowed": ["off", "ask", "always"], "default": "off", "section": "Video",
     "label": "Narrated summary video",
     "help": "A storyboard, offline renders, local speech and a reel. Each missing prerequisite refuses by design.",
     "reason": "Off: a fresh machine usually lacks ffmpeg, a local voice model or renderable media.",
     "cost": "Renderer time and model usage for image review; not measured."},
    {"key": "video.maxSeconds", "type": "number", "default": None, "section": "Video", "label": "Longest video (s)",
     "defaultText": "the skill's value", "help": "Trailing finding chapters over it are dropped.",
     "reason": "The skill's value.", "cost": "-", "when": {"video.make": ["ask", "always"]}},
    {"key": "video.footage", "type": "enum", "allowed": ["offline-renders", "offline-and-game"],
     "default": "offline-renders", "section": "Video", "label": "Footage",
     "help": "Offline renders only, or also in-game footage (launches the game on a scratch instance).",
     "reason": "Offline renders: making a report never authorizes a game launch by itself.", "cost": "-",
     "when": {"video.make": ["ask", "always"]}},
    {"key": "video.narration.engine", "type": "enum", "allowed": ["kokoro", "qwen3", "voxcpm2"], "default": None,
     "section": "Video", "label": "Narration engine", "defaultText": "kokoro",
     "help": "Which local speech model reads the script. A missing model is a refusal, never another narrator.",
     "reason": "The speech tool's default.", "cost": "-", "when": {"video.make": ["ask", "always"]}},
    {"key": "video.narration.voice", "type": "string", "default": None, "section": "Video", "label": "Voice",
     "defaultText": "bm_george", "help": "The engine's voice name.", "reason": "The speech tool's default.",
     "cost": "-", "when": {"video.make": ["ask", "always"]}},
    {"key": "video.narration.device", "type": "string", "default": None, "section": "Video", "label": "Device",
     "defaultText": "cpu", "help": "cpu or a GPU device the engine accepts.", "reason": "The speech tool's default.",
     "cost": "-", "when": {"video.make": ["ask", "always"]}},
    {"key": "video.narration.modelDir", "type": "path", "default": None, "section": "Video",
     "label": "Voice model folder", "defaultText": "not set",
     "help": "The complete local model folder; the speech tool never downloads.", "reason": "None until set.",
     "cost": "-", "when": {"video.make": ["ask", "always"]}},
    {"key": "video.narration.modelRevision", "type": "string", "default": None, "section": "Video",
     "label": "Voice model revision", "defaultText": "not set", "help": "The model folder's immutable revision.",
     "reason": "None until set.", "cost": "-", "when": {"video.make": ["ask", "always"]}},
    {"key": "video.ffmpeg", "type": "path", "default": None, "section": "Video", "label": "ffmpeg",
     "defaultText": "discovered (PATH or a bundled copy)", "env": "HOUSECARL_FFMPEG",
     "help": "Which ffmpeg cuts the reel.", "reason": "Discovered.", "cost": "-",
     "when": {"video.make": ["ask", "always"]}},
    # -- models -------------------------------------------------------------------------------------------------
    {"key": "models.recordChecks", "type": "model", "default": "session", "section": "Models",
     "label": "Record and asset checks", "help": "The model a delegated record/asset check runs on, through the plugin's agent for it.",
     "reason": "Same as the session.", "cost": "Not measured."},
    {"key": "models.imageReview", "type": "model", "default": "session", "section": "Models",
     "label": "Blind image review of video scenes", "help": "Needs an image-capable model, in a fresh agent.",
     "reason": "Same as the session.", "cost": "Not measured.", "when": {"video.make": ["ask", "always"]}},
    {"key": "models.flipPlan", "type": "model", "default": "session", "section": "Models",
     "label": "Writing the live check's flip plan", "help": "From the census and the scripts' source.",
     "reason": "Same as the session.", "cost": "Not measured.",
     "when": {"evaluation.liveSwitchCheck": ["ask", "always"]}},
    {"key": "models.rewriteExplanations", "type": "model", "default": "off", "allowOff": True, "section": "Models",
     "label": "Rewriting finding explanations",
     "help": "An optional model rewrites finding sentences; a rewrite is accepted only where every number and name is bound.",
     "reason": "Off: the template sentences are already plain.", "cost": "Not measured."},
    {"key": "models.checkExplanations", "type": "model", "default": "session", "section": "Models",
     "label": "Checking rewritten explanations",
     "help": "The fidelity judge: a rewritten sentence is accepted only where this model says it keeps the meaning.",
     "reason": "Same as the session; a Fable session runs it on Opus.", "cost": "Not measured.",
     "neverModel": "fable", "fallbackModel": "claude-opus-5-5"},
]
BY_KEY = {o["key"]: o for o in OPTIONS}
SOURCES = ("flag", "environment", "file", "built-in default")


class SettingsError(Exception):
    """A bad settings file or value; the message is the one sentence the user reads."""


# -------------------------------------------------------------------------------------------------- location --

def resolve_path(explicit: str | None = None) -> Path:
    if explicit:
        return Path(explicit)
    env = os.environ.get("HOUSECARL_EVAL_SETTINGS")
    if env and env.strip():
        return Path(env.strip())
    data = os.environ.get("HOUSECARL_DATA_DIR")
    if data and data.strip():
        return Path(data.strip()) / FILE_NAME
    if os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        return Path(os.environ["LOCALAPPDATA"]) / "houseCARL" / FILE_NAME
    xdg = os.environ.get("XDG_STATE_HOME")
    if xdg:
        return Path(xdg) / "housecarl" / FILE_NAME
    return Path.home() / ".local" / "state" / "housecarl" / FILE_NAME


# ------------------------------------------------------------------------------------------------ validation --

def _get(doc: dict, key: str):
    cur = doc
    for part in key.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return None
        cur = cur[part]
    return cur


def _put(doc: dict, key: str, value) -> None:
    parts = key.split(".")
    cur = doc
    for part in parts[:-1]:
        cur = cur.setdefault(part, {})
    if value is None:
        cur.pop(parts[-1], None)
    else:
        cur[parts[-1]] = value


def _leaves(obj, prefix=""):
    for k, v in obj.items():
        key = f"{prefix}{k}"
        if isinstance(v, dict) and key not in BY_KEY:
            yield from _leaves(v, key + ".")
        else:
            yield key, v


def model_choices(o: dict) -> list[str]:
    """The values a `models.*` option accepts: session, off where allowed, and each ID with a shipped agent."""
    return ["session"] + (["off"] if o.get("allowOff") else []) + list(MODEL_AGENTS)


def _choices_text(o: dict) -> str:
    c = model_choices(o)
    return ", ".join(c[:-1]) + " or " + c[-1] if len(c) > 1 else c[0]


def check_value(key: str, value, where: str) -> None:
    o = BY_KEY.get(key)
    if o is None:
        raise SettingsError(f"{where} sets `{key}`, which is not an evaluation setting this houseCARL knows: "
                            f"remove it, or reopen the window (`evalsettings.py window`).")
    if value is None:
        return
    t = o["type"]
    ok = True
    if t == "enum":
        if value not in o["allowed"]:
            raise SettingsError(f"{where} sets `{key}` to `{value}`, which is not one of "
                                f"{', '.join(o['allowed'])}: fix that value or reopen the window.")
    elif t == "bool":
        ok = isinstance(value, bool)
    elif t == "int":
        ok = isinstance(value, int) and not isinstance(value, bool) and value > 0
    elif t == "number":
        ok = isinstance(value, (int, float)) and not isinstance(value, bool) and value > 0
    elif t in ("path", "string"):
        ok = isinstance(value, str) and value.strip() != ""
    elif t == "model":
        ok = isinstance(value, str)
        if ok and value not in model_choices(o):
            why = ("but this step cannot be turned off" if value == "off"
                   else "a short name the host can resolve to an older model" if not value.startswith("claude-")
                   else "which has no houseCARL agent to run it")
            raise SettingsError(f"{where} sets `{key}` to `{value}`, {why}: run `evalsettings.py set {key}=VALUE` "
                                f"with VALUE one of {_choices_text(o)}.")
    if not ok:
        want = {"bool": "true or false", "int": "a whole number above zero", "number": "a number above zero",
                "path": "a path", "string": "a non-empty text", "model": _choices_text(o)}[t]
        raise SettingsError(f"{where} sets `{key}` to `{json.dumps(value)}`, which is not {want}: fix that value or "
                            f"reopen the window.")


def check_combination(values: dict, where: str) -> None:
    """Rules across options, on the EFFECTIVE values (so a flag that combines badly with the file is caught too)."""
    depth, live = values.get("evaluation.depth"), values.get("evaluation.liveSwitchCheck")
    if depth == "standard" and live in ("ask", "always"):
        raise SettingsError(f"{where} sets the depth to Standard, which judges the load-time state only, so the live "
                            f"switch check cannot be set to {live}: choose Deep, or set the live switch check to off.")
    if live == "always":
        missing = [k.split(".")[1] for k in ("liveSwitchCheck.instance", "liveSwitchCheck.shortcut",
                                             "liveSwitchCheck.gameExe") if not values.get(k)]
        if missing:
            raise SettingsError(f"{where} opts every evaluation into the live switch check, but no scratch "
                                f"{', '.join(missing)} is set: set them in the window, or switch the check to off or ask.")


def validate_doc(doc, where: str) -> dict:
    """The document's chosen values {key: value} (nulls dropped); raises SettingsError with the one sentence."""
    if not isinstance(doc, dict):
        raise SettingsError(f"{where} is not a JSON object: fix it, or run `evalsettings.py window` to replace it.")
    sv = doc.get("schemaVersion")
    if not isinstance(sv, int) or isinstance(sv, bool):
        raise SettingsError(f"{where} has no whole-number schemaVersion: fix it, or run `evalsettings.py window` "
                            f"to replace it.")
    if sv > SCHEMA_VERSION:
        raise SettingsError(f"{where} was written by a newer houseCARL (schema {sv}): update houseCARL, or pass "
                            f"--settings with a file this version can read.")
    if sv < SCHEMA_VERSION:
        raise SettingsError(f"{where} carries schema {sv}, which no houseCARL wrote: fix it, or run "
                            f"`evalsettings.py window` to replace it.")
    chosen = {}
    for section, body in doc.items():
        if section in ("schemaVersion", "saved"):
            continue
        if not isinstance(body, dict):
            check_value(section, None, where)
            raise SettingsError(f"{where} sets `{section}` to a value where a section belongs: fix it or reopen the window.")
        for key, value in _leaves(body, section + "."):
            check_value(key, value, where)
            if value is not None:
                chosen[key] = value
    return chosen


def read_json(path: Path) -> tuple[dict | None, bytes | None]:
    """(parsed JSON, raw bytes) with no option checks, or (None, None) when absent. Raises SettingsError on bad JSON."""
    if not path.exists():
        return None, None
    raw = path.read_bytes()
    try:
        return json.loads(raw.decode("utf-8-sig")), raw
    except (UnicodeDecodeError, json.JSONDecodeError) as ex:
        line = getattr(ex, "lineno", None)
        raise SettingsError(f"The evaluation settings file `{path}` is not valid JSON" + (f" (line {line})" if line else "")
                            + ": fix it, or run `evalsettings.py window` to replace it.") from None


def read_file(path: Path) -> tuple[dict | None, bytes | None]:
    """(document, raw bytes), or (None, None) when the file is absent. Raises SettingsError on a bad file."""
    doc, raw = read_json(path)
    if doc is None:
        return None, None
    where = f"The evaluation settings file `{path}`"
    chosen = validate_doc(doc, where)
    eff = {o["key"]: chosen.get(o["key"], o["default"]) for o in OPTIONS}
    check_combination(eff, where)
    return doc, raw


# ------------------------------------------------------------------------------------------------ resolution --

class Resolved:
    """Every option's value and source for one file and one set of flags."""

    def __init__(self, path: Path, doc: dict | None, raw: bytes | None, flags: dict | None = None,
                 env: dict | None = None):
        self.path, self.doc = path, doc
        self.sha256 = hashlib.sha256(raw).hexdigest() if raw is not None else None
        env = os.environ if env is None else env
        chosen = validate_doc(doc, f"The evaluation settings file `{path}`") if doc is not None else {}
        self.options = {}
        for o in OPTIONS:
            k = o["key"]
            if flags and flags.get(k) is not None:
                self.options[k] = {"value": flags[k], "source": "flag"}
            elif o.get("env") and env.get(o["env"]):
                self.options[k] = {"value": env[o["env"]], "source": "environment"}
            elif k in chosen:
                self.options[k] = {"value": chosen[k], "source": "file"}
            else:
                self.options[k] = {"value": o["default"], "source": "built-in default"}
        known_at_save = ((doc or {}).get("saved") or {}).get("options")
        self.new_options = ([o["key"] for o in OPTIONS if o["key"] not in known_at_save]
                            if isinstance(known_at_save, list) else [])
        check_combination(self.values(), f"The evaluation settings file `{path}`" if doc is not None
                          else "This evaluation's flags")

    def value(self, key):
        return self.options[key]["value"]

    def values(self) -> dict:
        return {k: v["value"] for k, v in self.options.items()}

    @property
    def source(self) -> str:
        return "file" if self.doc is not None else "built-in defaults"

    def record(self) -> dict:
        """What a plan freezes: the file's identity and every option's value and source."""
        return {"path": str(self.path), "sha256": self.sha256,
                "schemaVersion": (self.doc or {}).get("schemaVersion") if self.doc is not None else None,
                "source": self.source, "options": self.options, "newOptions": self.new_options}

    def sentences(self) -> list[str]:
        out = []
        if self.doc is None:
            out.append(missing_sentence())
        for k in self.new_options:
            out.append(f"This houseCARL adds the option `{k}`, which your saved settings do not set, so its default "
                       f"(`{json.dumps(BY_KEY[k]['default']) if BY_KEY[k]['default'] is not None else BY_KEY[k].get('defaultText', 'the tool default')}`) applies.")
        return out


def missing_sentence() -> str:
    return ("No saved evaluation settings were found, so this evaluation uses the built-in defaults (Deep depth, live "
            "switch check off, no video); run `evalsettings.py window` to choose.")


def load(explicit: str | None = None, flags: dict | None = None) -> Resolved:
    """The resolved settings for an evaluation command; raises SettingsError on a bad file or combination."""
    path = resolve_path(explicit)
    doc, raw = read_file(path)
    return Resolved(path, doc, raw, flags)


def tool_defaults(keys: list[str], explicit: str | None = None) -> dict:
    """For an evaluation tool's own flags: each key's value from the environment, the file or the built-in default
    (the tool applies its flag first). Raises SettingsError on a bad file, as every evaluation command does."""
    r = load(explicit)
    return {k: r.value(k) for k in keys}


def write_atomic(path: Path, doc: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".evalsettings-", suffix=".json", dir=str(path.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(doc, indent=2, ensure_ascii=False) + "\n")
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def stamp(doc: dict, by: str) -> dict:
    doc = dict(doc)
    doc["schemaVersion"] = SCHEMA_VERSION
    doc["saved"] = {"utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "by": by,
                    "houseCARL": houseCARL_version(), "options": [o["key"] for o in OPTIONS]}
    return doc


def houseCARL_version() -> str:
    try:
        manifest = json.loads((SW.CHECKOUT / "external" / "houseCARL" / "plugin" / ".claude-plugin" / "plugin.json").read_text(encoding="utf-8"))
        return str(manifest.get("version") or "unknown")
    except (OSError, ValueError):
        return "unknown"


def merge_chosen(base: dict | None, chosen: dict) -> dict:
    doc = {k: v for k, v in (base or {}).items() if k not in ("schemaVersion", "saved")}
    doc = json.loads(json.dumps(doc))
    for k, v in chosen.items():
        _put(doc, k, v)
    for section in list(doc):
        if isinstance(doc[section], dict) and not doc[section]:
            doc.pop(section)
    return doc


def parse_value(key: str, text: str):
    o = BY_KEY.get(key)
    if text in ("null", ""):
        return None
    if o and o["type"] == "bool":
        if text.lower() in ("true", "false"):
            return text.lower() == "true"
        return text
    if o and o["type"] in ("int", "number"):
        try:
            v = float(text)
            return int(v) if v.is_integer() and o["type"] == "int" else v
        except ValueError:
            return text
    return text


# ---------------------------------------------------------------------------------------------------- window --

def page_html(resolved: Resolved, token: str | None, save_url: str | None, close_url: str | None) -> str:
    chosen = {}
    if resolved.doc is not None:
        chosen = {k: v for k, v in _leaves({k: v for k, v in resolved.doc.items() if k not in ("schemaVersion", "saved")})
                  if v is not None}
    block = {"token": token, "saveUrl": save_url, "closeUrl": close_url, "file": str(resolved.path),
             "values": {k: v for k, v in (merge_chosen(None, chosen)).items()},
             "chosen": sorted(chosen), "newOptions": resolved.new_options,
             "effective": resolved.values(),
             "options": [dict(o, allowed=model_choices(o)) if o["type"] == "model" else o for o in OPTIONS],
             "browser": detect_browser(),
             "ffmpeg": detect_ffmpeg()}
    text = TEMPLATE.read_text(encoding="utf-8")
    data = json.dumps(block, ensure_ascii=False).replace("</", "<\\/")
    return text.replace("/*HC_SETTINGS*/null", data).replace("{{FILE}}", html.escape(str(resolved.path)))


def detect_browser() -> str | None:
    """The browser the report tool would print with (its own resolution order), or None."""
    import evalreport  # lazy: evalreport reads this module for its defaults
    path, _, _ = evalreport.resolve_browser()
    return str(path) if path else None


def detect_ffmpeg() -> str | None:
    """The ffmpeg the reel tool would use (its own resolution order, the saved choice first), or None."""
    import evalreel  # lazy: evalreel reads this module for its defaults
    found = evalreel.ffmpeg_find(None)
    return found["exe"] if found else None


class _Server(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = False


def serve_window(resolved: Resolved, opener: str | None, timeout_s: float) -> str:
    """Serve the page once on 127.0.0.1; 'saved', 'closed' or 'timeout'. Writes the file only on a valid Save."""
    token = secrets.token_urlsafe(24)
    outcome = {"state": None}
    done = threading.Event()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *a):  # quiet
            pass

        def _token_ok(self) -> bool:
            q = parse_qs(urlparse(self.path).query)
            return secrets.compare_digest((q.get("token") or [""])[0], token)

        def _send(self, code: int, body: str, ctype="application/json; charset=utf-8"):
            data = body.encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if urlparse(self.path).path != "/" or not self._token_ok():
                return self._send(403, json.dumps({"error": "this page needs the address evalsettings printed"}))
            self._send(200, page_html(resolved, token, f"/save?token={token}", f"/close?token={token}"),
                       "text/html; charset=utf-8")

        def do_POST(self):
            route = urlparse(self.path).path
            if route not in ("/save", "/close") or not self._token_ok():
                return self._send(403, json.dumps({"error": "refused: no valid token"}))
            n = int(self.headers.get("Content-Length") or 0)
            try:
                body = json.loads(self.rfile.read(n).decode("utf-8") or "{}") if n else {}
            except (UnicodeDecodeError, json.JSONDecodeError):
                return self._send(400, json.dumps({"error": "the body is not JSON"}))
            if route == "/close":
                outcome["state"] = outcome["state"] or "closed"
                self._send(200, json.dumps({"ok": True}))
                done.set()
                return
            settings = body.get("settings") if isinstance(body, dict) else None
            if not isinstance(settings, dict):
                return self._send(400, json.dumps({"error": "the body carries no settings object"}))
            try:
                doc = stamp(settings, "window")
                validate_doc(doc, "The window's settings")
                chosen = validate_doc(doc, "The window's settings")
                check_combination({o["key"]: chosen.get(o["key"], o["default"]) for o in OPTIONS},
                                  "The window's settings")
            except SettingsError as ex:
                return self._send(400, json.dumps({"error": str(ex)}))
            write_atomic(resolved.path, doc)
            outcome["state"] = "saved"
            self._send(200, json.dumps({"ok": True, "file": str(resolved.path)}))
            done.set()

    srv = _Server(("127.0.0.1", 0), Handler)
    port = srv.server_address[1]
    url = f"http://127.0.0.1:{port}/?token={token}"
    t = threading.Thread(target=srv.serve_forever, kwargs={"poll_interval": 0.2}, daemon=True)
    t.start()
    try:
        if opener:
            subprocess.Popen([sys.executable, opener, url])
            print(f"evaluation settings window: {url}")
        else:
            opened = False
            try:
                opened = webbrowser.open(url)
            except webbrowser.Error:
                opened = False
            print(("Opened the evaluation settings window: " if opened else
                   "No browser could be opened; open this address to choose the evaluation settings: ") + url)
        sys.stdout.flush()
        if not done.wait(timeout_s):
            outcome["state"] = "timeout"
    finally:
        srv.shutdown()
        srv.server_close()
    return outcome["state"] or "closed"


def desktop_session() -> bool:
    if os.name == "nt" or sys.platform == "darwin":
        return True
    return bool(os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"))


# -------------------------------------------------------------------------------------------------- commands --

def cmd_path(a, run) -> int:
    print(resolve_path(a.settings))
    return EXIT_OK


def cmd_show(a, run) -> int:
    r = load(a.settings)
    if a.json:
        print(json.dumps({"file": str(r.path), "fileState": "present" if r.doc is not None else "missing",
                          "sha256": r.sha256, "options": r.options, "newOptions": r.new_options}, indent=1))
        return EXIT_OK
    print(f"evaluation settings: {r.path} ({'present' if r.doc is not None else 'missing: built-in defaults'})")
    for o in OPTIONS:
        v = r.options[o["key"]]
        shown = v["value"] if v["value"] is not None else f"({o.get('defaultText', 'tool default')})"
        print(f"   {o['key']:34} {str(shown):30} {v['source']}")
    for s in r.sentences():
        print(s)
    return EXIT_OK


def cmd_ensure(a, run) -> int:
    path = resolve_path(a.settings)
    r = load(a.settings)
    if r.doc is not None:
        print(f"Evaluation settings: {path}")
        return EXIT_OK
    unattended = os.environ.get("HOUSECARL_EVAL_UNATTENDED", "").strip() not in ("", "0")
    if a.no_window or unattended or not (a.opener or desktop_session()):
        print(missing_sentence())
        return EXIT_OK
    state = serve_window(r, a.opener, a.timeout_seconds or DEFAULT_TIMEOUT_S)
    run.note(f"window {state}")
    if state == "saved":
        print(f"Saved the evaluation settings to {path}")
    else:
        print(("The settings window was closed without Save" if state == "closed" else
               "The settings window was left open past its timeout and nothing was saved") + "; " + missing_sentence())
    return EXIT_OK


def cmd_window(a, run) -> int:
    r = load(a.settings)
    if a.page_only:
        Path(a.page_only).write_text(page_html(r, None, None, None), encoding="utf-8")
        print(f"wrote the settings page (no server; it shows the set command to paste): {a.page_only}")
        return EXIT_OK
    state = serve_window(r, a.opener, a.timeout_seconds or DEFAULT_TIMEOUT_S)
    print({"saved": f"Saved the evaluation settings to {r.path}",
           "closed": "The settings window was closed without Save; nothing changed.",
           "timeout": "The settings window was left open past its timeout; nothing changed."}[state])
    return EXIT_OK


def cmd_set(a, run) -> int:
    path = resolve_path(a.settings)
    # The merged result is what is checked, so `set` can repair the very value that makes the saved file refused.
    doc, _ = read_json(path)
    if doc is not None and not isinstance(doc, dict):
        raise SettingsError(f"The evaluation settings file `{path}` is not a JSON object: fix it, or run "
                            f"`evalsettings.py window` to replace it.")
    chosen = {}
    for pair in a.pairs:
        key, sep, text = pair.partition("=")
        if not sep:
            raise SettingsError(f"`{pair}` is not KEY=VALUE: write it as, for example, evaluation.depth=standard.")
        key = key.strip()
        val = parse_value(key, text.strip())
        check_value(key, val, "This command")
        chosen[key] = val
    new = stamp(merge_chosen(doc, chosen), "set")
    eff = validate_doc(new, "This command")
    check_combination({o["key"]: eff.get(o["key"], o["default"]) for o in OPTIONS}, "This command")
    write_atomic(path, new)
    print(f"Saved {', '.join(chosen)} to {path}")
    return EXIT_OK


def cmd_defaults(a, run) -> int:
    doc = {}
    for o in OPTIONS:
        parts = o["key"].split(".")
        cur = doc
        for p in parts[:-1]:
            cur = cur.setdefault(p, {})
        cur[parts[-1]] = None
    out = Path(a.out)
    write_atomic(out, stamp(doc, "defaults"))
    print(f"Wrote an explicit built-in-defaults settings file: {out}")
    return EXIT_OK


def cmd_validate(a, run) -> int:
    path = Path(a.file)
    if not path.exists():
        raise SettingsError(f"The evaluation settings file `{path}` does not exist: check the path.")
    read_file(path)
    print(f"valid: {path}")
    return EXIT_OK


def cmd_route(a, run) -> int:
    o = BY_KEY.get(a.key)
    if o is None or o["type"] != "model":
        raise SettingsError(f"`{a.key}` is not a model setting: route one of "
                            f"{', '.join(x['key'] for x in OPTIONS if x['type'] == 'model')}.")
    r = load(a.settings)
    v = r.options[a.key]
    out = {"key": a.key, "value": v["value"], "source": v["source"]}
    if v["value"] == "off":
        out["use"] = "off"
    elif o.get("neverModel") and o["neverModel"] in str(v["value"]).lower():
        raise SettingsError(f"`{a.key}` never runs on a {o['neverModel'].title()} model: set it to session or "
                            f"{o['fallbackModel']}.")
    elif v["value"] == "session" and o.get("neverModel"):
        sm = (a.session_model or "").strip()
        if not sm:
            raise SettingsError(f"`{a.key}` never runs on a {o['neverModel'].title()} model: pass --session-model "
                                f"with this session's full model ID.")
        if o["neverModel"] in sm.lower():
            if os.environ.get("CLAUDECODE") != "1":
                raise SettingsError(f"`{a.key}` must not run on the session's model {sm}, and only Claude Code can "
                                    f"start an agent on {o['fallbackModel']}: run the evaluation in Claude Code.")
            out.update(use="agent", agent=f"{_plugin_name()}:{MODEL_AGENTS[o['fallbackModel']]}",
                       note=f"the session runs {sm}, which {a.key} never uses; it runs on {o['fallbackModel']}")
            print(f"NOTE: {out['note']}", file=sys.stderr)
        else:
            out.update(use="session", agent="general-purpose")
    elif v["value"] == "session":
        out.update(use="session", agent="general-purpose")
    else:
        # Only Claude Code starts an agent on a named model; it marks its commands' environment with CLAUDECODE=1.
        if os.environ.get("CLAUDECODE") != "1":
            raise SettingsError(f"`{a.key}` names {v['value']}, but only Claude Code can start an agent on a named "
                                f"model and this command is not running under it: set `{a.key}` to session, or run "
                                f"the evaluation in Claude Code.")
        out.update(use="agent", agent=f"{_plugin_name()}:{MODEL_AGENTS[v['value']]}")
    run.note(f"{a.key} -> {out['use']}")
    print(json.dumps(out))
    return EXIT_OK


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="evalsettings", description=__doc__.split("\n\n")[0])
    ap.add_argument("--settings", help="the settings file to use (first in the resolution order)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("path").set_defaults(fn=cmd_path)
    p = sub.add_parser("show")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_show)
    for name, fn in (("ensure", cmd_ensure), ("window", cmd_window)):
        p = sub.add_parser(name)
        p.add_argument("--opener", help="run `python SCRIPT URL` instead of the browser (tests)")
        p.add_argument("--timeout-seconds", type=float,
                       help=f"stop the server after this long with nothing saved (default {DEFAULT_TIMEOUT_S:.0f})")
        if name == "ensure":
            p.add_argument("--no-window", action="store_true")
        else:
            p.add_argument("--page-only", metavar="FILE", help="write the page without a server (it shows the set command)")
        p.set_defaults(fn=fn)
    p = sub.add_parser("set")
    p.add_argument("pairs", nargs="+")
    p.set_defaults(fn=cmd_set)
    p = sub.add_parser("defaults")
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_defaults)
    p = sub.add_parser("validate")
    p.add_argument("file")
    p.set_defaults(fn=cmd_validate)
    p = sub.add_parser("route")
    p.add_argument("key", help="a models.* setting")
    p.add_argument("--session-model", default=None,
                   help="this session's full model ID; required for a setting that never runs on some models")
    p.set_defaults(fn=cmd_route)
    return ap


def main(argv=None) -> int:
    a = build_parser().parse_args(argv)
    with Run("evalsettings", mode=a.cmd) as run:
        try:
            rc = a.fn(a, run)
        except SettingsError as ex:
            print(str(ex), file=sys.stderr)
            run.refuse(str(ex), exit_code=EXIT_REFUSED)
            return EXIT_REFUSED
        run.complete(exit_code=rc)
        return rc


if __name__ == "__main__":
    sys.exit(main())
