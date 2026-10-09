"""evalrun — the evaluation controller (roadmap 4.23, Q42).

    python evalrun.py plan   --registry R --scope scope.json --candidates DIR --out RUN [...]
    python evalrun.py run    --run RUN
    python evalrun.py resume --run RUN [--rebuild-ledger]
    python evalrun.py finalize --run RUN
    python evalrun.py bundle --run RUN [--out DIR] [--media media.json]
    python evalrun.py verify-bundle DIR
    python evalrun.py status --run RUN

What this is for. The tools can already measure a load order; what still lived in the coordinating
model was remembering WHICH checks an evaluation needs, collecting their outputs, and telling a
successful narrow run from an incomplete evaluation. This controller makes that bookkeeping
deterministic: it derives a check plan from a versioned registry and a MEASURED scope, freezes the
plan, executes the supported checks as child processes through the existing binaries, validates
every receipt against a closed vocabulary of rejection causes, resumes without mixing snapshots,
and finalizes a disposition the model cannot inflate. `standards/HOUSECARL_EVALUATION_BUNDLE.md` is
the contract; this file implements it and does not define a second one.

What it deliberately does NOT do. It does not detect anything itself: every conclusion comes from a
probe's own emit, from `conflictdiff`'s own verdict line, or from `ordersweep.py`'s own
classification, retained verbatim and scoped to the probes that ran. It does not sum unlike
coverage units, it does not net a repair against a regression, and it never promotes a bounded
verdict into a whole-evaluation pass. It launches no game, opens no network connection (an
adviceprobe adapter is forced offline here), and writes no MO2 profile.

Composition. `--base`-capable emitting probes run through `ordersweep.py` ONCE, as a single child
writing a folder of its own (`RUN/sweep/inv-NNNN`, never rewritten by a later launch), and the job
records, summary and emits of THAT launch are split into one receipt per (check, arm, part). Everything else
runs through a thin generic adapter whose probe-specific flags come from the registry as data, so
the tool surface stays generic and the domain knowledge stays in the skill.

Every child process is a `scripts/runlog.py` `Runner` job, so lifecycle, child attribution, live
output and retry accounting come from the one adapter rather than from anyone remembering them.
Unless the caller set it, the run store is `RUN/runlog`, so a run's timing history stays with the
run instead of mixing into the shared store.

Exit codes: 0 ok; 2 usage or a refused input; 3 a finalize or verify-bundle refusal.
"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run, Runner  # noqa: E402

sys.path.insert(0, str(HERE))
import evalregistry  # noqa: E402

#: Schema 2 is the evidence-chain schema: the plan freezes a per-arm REQUESTED identity, receipts record
#: their comparison inputs, and finalize/bundle/verify walk the receipt-to-evidence chain. A schema-1
#: artifact carries none of that, so it is refused BY NAME rather than certified by a reader that would
#: silently find nothing to check.
SCHEMA_VERSION = 2
BUNDLE_SCHEMA = 2
#: D1: what a plan's `arms[].requested.providers` means. `arm-view`: the providers THAT arm's configuration
#: reads — its own `Mods` view when the candidate declares one. A plan without this marker froze the scope's
#: providers onto every arm, so `run`, `resume` and `import-results` refuse it by name (`request_refusal`).
REQUEST_PROVIDERS = "arm-view"

# Exit codes, spelled once.
EXIT_OK = 0
EXIT_REFUSED_INPUT = 2
EXIT_REFUSED_RESULT = 3

# A refusal MARKER, anchored at the start of a log line: `REFUSED`, `! ...REFUSED`, `MISSING MASTER`.
# The word inside a probe's own prose ("with a function the decompiler refused 0") is not a refusal,
# and rejecting a receipt that exited 0 with a valid output because of it is a false negative.
REFUSED_RE = re.compile(r"^.{0,40}?(REFUSED|MISSING MASTER)", re.M)

#: How much of conflictdiff's refusal a diff receipt quotes; its longest (no configuration) is 392 characters.
REFUSAL_QUOTE_CHARS = 600


def conflictdiff_refusal(log_text: str) -> str | None:
    """conflictdiff's reason for not comparing two emits, as one paragraph, from its job log (None if none found).
    Its forms: the configuration gate's indented lines ending `REFUSED.` (exit 3); the population/state gate's `! ...`
    line and its indented continuation lines (exit 3); a usage or read error on stderr (exit 2), which the job log
    keeps after `---- stderr ----`."""
    text = log_text or ""
    out, _, err = text.partition("\n---- stderr ----\n")
    lines = out.splitlines()
    ends = [i for i, ln in enumerate(lines) if ln.rstrip().endswith("REFUSED.") and ln[:1].isspace()]
    if ends:
        i = ends[-1]
        start = i
        while start > 0 and lines[start - 1][:1].isspace() and lines[start - 1].strip():
            start -= 1
        return " ".join(ln.strip() for ln in lines[start:i + 1])
    for i, ln in enumerate(lines):
        if ln.strip().startswith("!"):
            indent = len(ln) - len(ln.lstrip())
            block = [ln.strip()]
            for nxt in lines[i + 1:]:
                if not nxt.strip() or nxt.strip().startswith("!") or len(nxt) - len(nxt.lstrip()) <= indent:
                    break
                block.append(nxt.strip())
            return " ".join(block)
    return next((ln.strip() for ln in err.splitlines() if ln.strip()), None)

# A shareable report package carries identities, never payloads. Anything with one of these
# suffixes is referred to by name and hash and is never copied into a bundle.
BINARY_SUFFIXES = {".esp", ".esm", ".esl", ".bsa", ".ba2", ".nif", ".dds", ".hkx", ".wav",
                   ".xwm", ".fuz", ".exe", ".dll", ".pex", ".tri", ".seq", ".bik", ".lod"}

# Closed vocabularies (standards/HOUSECARL_EVALUATION_BUNDLE.md). Spelled here so a typo is a
# KeyError at the call site rather than an invented status in a bundle.
REJECTION_CAUSES = (
    "exit-nonzero", "refused", "illegal-configuration", "output-missing", "output-stale",
    "schema-unsupported", "identity-missing", "identity-mismatch", "wrong-check",
    "duplicate-receipt", "foreign-run", "population-mismatch", "coverage-missing",
    "extraction-failed", "stale-inputs", "mixed-snapshot", "budget-exhausted",
    "blocked-prerequisite", "completion-missing",
)
#: Kinds that consume a load order, and can therefore refuse the CONFIGURATION itself. A refusal
#: from a probe that never reads an order (adviceprobe, patchprobe) is just `refused`.
ORDER_CONSUMING_KINDS = {"scopeprobe", "ordersweep-probe", "probe-emit", "probe-json"}

TERMINAL_OK = ("completed", "reused")
#: Plan row statuses that wait for imported evidence: `mcp-only` (an MCP tool's capture) and `live-import` (a live
#: game driver's receipt, read by evallive.py). The run never measures either; only `import-results` completes one.
IMPORT_ROW_STATUSES = ("mcp-only", "live-import")
#: The plan row status of an opt-in check the caller did not ask for (`plan --opt-in`). Never blocking, always listed.
NOT_OPTED_IN = "not-opted-in"
CLAIMS = ("no-findings-in-completed-checks", "findings", "incomplete")
ARM_KINDS = ("baseline", "candidate", "supplied-combination")
PART_NAMES = (None, "arm", "reverse", "diff")


# ------------------------------------------------------------------------------- small helpers --

def utcnow() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def sha256_file(p: Path) -> str | None:
    try:
        h = hashlib.sha256()
        with open(p, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


SHA256_RE = re.compile(r"^[0-9a-fA-F]{64}$")


def is_sha256(value) -> bool:
    return isinstance(value, str) and bool(SHA256_RE.match(value))


def same_path(a, b) -> bool:
    """Two spellings of one file compared the way the filesystem under these tools does: separators
    unified, `.`/`..` folded, case ignored. `X:/inst\\mods\\M\\P.esp` and `X:\\inst\\mods\\M\\p.esp` are
    one file, and a plan that resolved the first must not read the second as a different copy."""
    def norm(p):
        s = str(p).replace("\\", os.sep).replace("/", os.sep)
        return os.path.normcase(os.path.normpath(s))
    return bool(a) and bool(b) and norm(a) == norm(b)


#: The configuration schema the fingerprint text is keyed on (`ConfigurationIdentity.CurrentSchema`).
CONFIG_SCHEMA = 1


def configuration_fingerprint(plugins, providers, overlays, population, state, schema=None) -> str:
    """`ConfigurationIdentity.Compute`'s canonical text, line for line.

    The one fingerprint rule lives in `_geometry-harness/_shared/Configuration.cs`
    (`ConfigurationIdentity.Compute`); this function is its only Python spelling in this controller, so
    a second convention cannot be invented here. The text is: `schema:<n>`, `population:<p>`,
    `state:<s>`, then one `plugin:<lowercase name>:<sha256>` per plugin IN ORDER, one
    `provider:<kind>:<lowercase name>` per provider, one `overlay:<kind>:<sha256>` per overlay; every
    line newline-terminated; the digest is sha256 hex, lower case. Paths are NOT in the text — names
    and content hashes are."""
    sb = [f"schema:{CONFIG_SCHEMA if schema is None else schema}\n",
          f"population:{population}\n", f"state:{state}\n"]
    for p in plugins or []:
        sb.append(f"plugin:{str(p.get('name') or '').lower()}:{p.get('sha256') or ''}\n")
    for v in providers or []:
        sb.append(f"provider:{v.get('kind') or ''}:{str(v.get('name') or '').lower()}\n")
    for o in overlays or []:
        sb.append(f"overlay:{o.get('kind') or ''}:{o.get('sha256') or ''}\n")
    return hashlib.sha256("".join(sb).encode("utf-8")).hexdigest()


class HashMemo:
    """One hash per (resolved path, size, mtime) per invocation: re-checking 80 plugins across 60
    receipts reads each file once, not sixty times."""

    def __init__(self):
        self.cache: dict = {}

    def sha256(self, p) -> str | None:
        p = Path(p)
        try:
            st = p.stat()
            key = (str(p.resolve()), st.st_size, st.st_mtime_ns)
        except OSError:
            return None
        if key not in self.cache:
            self.cache[key] = sha256_file(p)
        return self.cache[key]


def canonical(obj) -> str:
    """One spelling of a JSON value, so a hash over it is stable across machines."""
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def load_json(p: Path):
    try:
        return json.loads(Path(p).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def write_json(p: Path, obj) -> None:
    """Atomic by construction: a killed process never leaves a half-written ledger."""
    p = Path(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_suffix(p.suffix + ".tmp")
    tmp.write_text(json.dumps(obj, indent=1, ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, p)


def iso_of(ts: float) -> str:
    return datetime.fromtimestamp(ts, timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def parse_iso(text: str | None) -> float | None:
    if not text:
        return None
    t = text.strip().replace("Z", "+00:00")
    try:
        dt = datetime.fromisoformat(t)
    except ValueError:
        return None
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.timestamp()


def rel_to(run: Path, p: Path) -> str:
    """A RUN-relative path with forward slashes, so a receipt survives the directory moving."""
    try:
        return Path(p).resolve().relative_to(Path(run).resolve()).as_posix()
    except ValueError:
        return Path(p).as_posix()


def dotted(doc, path: str):
    """Read `a.b.c` out of a parsed JSON document; None when any step is absent."""
    cur = doc
    for part in path.split("."):
        if isinstance(cur, dict) and part in cur:
            cur = cur[part]
        elif isinstance(cur, list) and part.isdigit() and int(part) < len(cur):
            cur = cur[int(part)]
        else:
            return None
    return cur


def resolve_spec(spec: str, doc, log_text: str):
    """A-6's `<spec>`: a dotted path into the JSON output, or `regex:<pattern>` with a named group
    `n` over the child's LOG. Returns (count, why-it-failed). None means it did not resolve — which
    is `schema-unsupported`, never a zero."""
    if not isinstance(spec, str):
        return None, "the spec is not a string"
    if spec.startswith("regex:"):
        try:
            m = re.search(spec[len("regex:"):], log_text or "", re.M)
        except re.error as ex:
            return None, f"the pattern does not compile: {ex}"
        if not m:
            return None, "the pattern matched nothing in the child's log"
        try:
            return int(str(m.group("n")).replace(",", "")), ""
        except (IndexError, ValueError, re.error):
            return None, "the pattern matched but its group 'n' is not a number"
    if doc is None:
        return None, "the output is not JSON, so a dotted path cannot be read"
    val = dotted(doc, spec)
    if isinstance(val, bool):
        return None, "the path resolved to a boolean, not a count"
    if isinstance(val, int):
        return val, ""
    if isinstance(val, (list, dict)):
        return len(val), ""
    return None, "the path is absent from this output"


def refuse(message: str) -> int:
    """A refused input says what was wrong and exits 2 — never a degraded run."""
    print(f"REFUSED: {message}", file=sys.stderr)
    return EXIT_REFUSED_INPUT


def slug(text: str, n: int = 24) -> str:
    s = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    return s[:n].strip("-") or "x"


def exe_name(name: str) -> str:
    return f"{name}.exe" if os.name == "nt" else name


def probe_exe(harness: Path, probe: str) -> Path:
    return Path(harness) / probe / "bin" / "Release" / "net9.0" / exe_name(probe)


def tool_identity(harness: Path, probe: str) -> dict:
    """The code that ACTUALLY runs: the managed assembly beside the apphost, not only the exe
    (memory: an apphost hash does not change when the managed code does)."""
    exe = probe_exe(harness, probe)
    folder = exe.parent
    ident = {"exe": str(exe), "exeSha256": sha256_file(exe),
             "dllSha256": sha256_file(folder / f"{probe}.dll"),
             "coreDllSha256": sha256_file(folder / "housecarl-core.dll")}
    ident["present"] = ident["exeSha256"] is not None
    return ident


def argv_shape(argv: list[str], run: Path) -> list[str]:
    """The argv with volatile absolute paths reduced to a shape: a receipt must be comparable
    between two machines without pretending the paths were the same."""
    out = []
    for tok in argv:
        try:
            p = Path(tok)
        except (TypeError, ValueError):
            out.append(tok)
            continue
        if os.path.isabs(tok) or tok.startswith(("./", ".\\")):
            r = rel_to(run, p)
            out.append(f"<RUN>/{r}" if not os.path.isabs(r) and ".." not in r else f"<abs>/{p.name}")
        else:
            out.append(tok)
    return out


# ------------------------------------------------------------------------- registry adapter --
#
# Everything evalrun asks of evalregistry goes through this class, so the module's function names
# are coupled in ONE place. (Agent A owns evalregistry.py; this adapter accepts either the plain
# names or a `*_checks`-suffixed spelling, and refuses by name when neither is there.)

def topo_order(checks: list[dict]) -> list[dict]:
    """The registry's checks in prerequisite order, so a check really runs after the checks whose
    receipts it needs. evalregistry validates that there is no cycle; this only orders."""
    by_id = {c["id"]: c for c in checks}
    out, state = [], {}

    def visit(cid: str, chain: list[str]):
        if state.get(cid) == 2 or cid not in by_id:
            return
        if state.get(cid) == 1:
            raise ValueError("prerequisite cycle: " + " -> ".join(chain + [cid]))
        state[cid] = 1
        for pre in by_id[cid].get("prerequisites") or []:
            visit(pre, chain + [cid])
        state[cid] = 2
        out.append(by_id[cid])

    for c in checks:
        visit(c["id"], [])
    return out


class RegistryView:
    def __init__(self, path: Path):
        self.path = Path(path)
        self.sha256 = evalregistry.registry_sha256(str(self.path))
        self.doc = evalregistry.load(str(self.path))
        problems = list(evalregistry.validate(self.doc) or [])
        if problems:
            raise ValueError(f"registry {self.path} is invalid: " + "; ".join(problems))

    @property
    def version(self) -> int:
        return int(self.doc.get("registryVersion", 0))

    @property
    def checks(self) -> list[dict]:
        return topo_order(list(self.doc.get("checks", [])))

    @property
    def families(self) -> list[dict]:
        return evalregistry.families(self.doc)

    def applicability_map(self, scope: dict | None) -> dict:
        """checkId -> {applicability: yes | no | unknown, evidence}. Unknown never becomes no —
        the registry decides, this controller only records."""
        return evalregistry.applicability(self.doc, scope)


# ---------------------------------------------------------------------------------- the plan --

def arm_id(kind: str, stem: str, index: int) -> str:
    """Short, stable, and a path a receipt filename can live inside on Windows. The full candidate
    file name stays in the arm record; only the id is shortened."""
    return "baseline" if kind == "baseline" else stem


#: The file kinds a layer provides, from `HarnessOrderCli.BuildLayerMap`'s extension filter.
PLUGIN_SUFFIXES = (".esp", ".esm", ".esl")

_FOLDER_PLUGINS: dict[str, dict] = {}
_MODLISTS: dict[tuple, tuple] = {}


def _folder_plugins(folder: str) -> dict:
    """The `.esp/.esm/.esl` files at ONE folder's top level, keyed by lower-case name — the C#'s
    `Directory.GetFiles(dir)` inside its try/catch: an absent or unreadable folder is SKIPPED, which is
    not the same as a folder that provides nothing, but the layer map cannot tell the two apart either.
    Cached per folder: a profile lists hundreds of them and one plan reads each once."""
    hit = _FOLDER_PLUGINS.get(folder)
    if hit is None:
        hit = {}
        try:
            with os.scandir(folder) as it:
                for e in it:
                    if e.is_file() and os.path.splitext(e.name)[1].lower() in PLUGIN_SUFFIXES:
                        hit.setdefault(e.name.lower(), os.path.join(folder, e.name))
        except OSError:
            hit = {}
        _FOLDER_PLUGINS[folder] = hit
    return hit


def _modlist(instance: str, profile: str | None) -> tuple[list, list, bool, str | None]:
    """`(enabled, disabled, read, path)` for `<instance>/profiles/<profile>/modlist.txt`, mirroring
    `Mo2LoadOrder.ParseModlist` (`src/housecarl-core/Mo2LoadOrder.cs`): `+Name` is enabled, `-Name`
    disabled, `#` a comment, a `…_separator` name is skipped from BOTH lists, and the file's order is
    the priority order with the TOP line the highest. `read` is false when no profile was named or the
    file will not open — the layers it defines are then UNKNOWN, never empty."""
    key = (str(instance), str(profile or ""))
    hit = _MODLISTS.get(key)
    if hit is not None:
        return hit
    if not profile:
        hit = ([], [], False, None)
    else:
        path = os.path.join(instance, "profiles", str(profile), "modlist.txt")
        enabled, disabled = [], []
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                lines = f.read().splitlines()
        except OSError:
            hit = ([], [], False, path)
        else:
            for raw in lines:
                line = raw.rstrip()
                if not line or line[0] == "#" or line[0] not in "+-":
                    continue
                mod = line[1:].strip()
                if not mod or mod.lower().endswith("_separator"):
                    continue
                (enabled if line[0] == "+" else disabled).append(mod)
            hit = (enabled, disabled, True, path)
    _MODLISTS[key] = hit
    return hit


def plugin_layers(name: str, view_mods, *, instance: str | None, profile: str | None,
                  game: str | None) -> dict:
    """Every layer that provides this plugin file, in the probes' own precedence order.

    The one Python spelling of `HarnessOrderCli.BuildLayerMap`
    (`_geometry-harness/_shared/HarnessOrderCli.cs`), reading the profile through
    `Mo2LoadOrder.ParseModlist` (`src/housecarl-core/Mo2LoadOrder.cs`). The layers, in order:
    `<instance>/overwrite`; the VIEW's mod folders in priority order (the candidate's own `Mods` list
    if it has one, else the scope's providers of kind `mod` as recorded), each `<instance>/mods/<name>`;
    `<game>/Data`; the profile's ENABLED mods that are not in the view; then its DISABLED mods. First
    sighting of a file name wins, names compare case-insensitively, only `.esp/.esm/.esl` at a folder's
    top level count, and an absent or unreadable folder is skipped.

    The probes take the game folder from `HOUSECARL_GAME_PATH` (or the harness config, for its own instance) and ALWAYS walk its
    `Data`; a plan given no `--game` walks the other layers and says so in `gameWalked`, and one given no
    `--profile` cannot list the profile's folders at all (`profileNamed`). The caller states a hash only
    when the walk was whole enough to name the copy a probe would read.

    Returns `{"rows": [{layer, path, sha256}, …], "views": [str, …], "modlistRead", "modlistPath",
    "gameWalked", "profileNamed"}`.
    `rows` is in precedence order, so `rows[0]` is the copy a probe would read, and each `views` entry
    says `in-view` (layers 1–3), `out-of-view` (4–5) or `unknown`. An `unknown` row is a folder under
    `mods/` seen only because modlist.txt would not read: it is NOT a layer — without the modlist the
    probe has no priority order for those folders and refuses the order rather than pick a copy — and it
    is listed so the ambiguity is stated rather than guessed at."""
    out = {"rows": [], "views": [], "modlistRead": True, "modlistPath": None,
           "gameWalked": bool(game), "profileNamed": bool(profile)}
    if not instance:
        out["modlistRead"] = False
        return out
    plain = os.path.basename(str(name))
    key = plain.lower()
    in_view = {str(m).lower() for m in view_mods}
    layers: list[tuple[str, str, str]] = [("overwrite", os.path.join(instance, "overwrite"), "in-view")]
    for m in view_mods:
        layers.append((str(m), os.path.join(instance, "mods", str(m)), "in-view"))
    if game:
        layers.append(("Data", os.path.join(game, "Data"), "in-view"))
    enabled, disabled, modlist_read, modlist_path = _modlist(instance, profile)
    out["modlistRead"], out["modlistPath"] = modlist_read, modlist_path
    for m in list(enabled) + list(disabled):
        if str(m).lower() not in in_view:
            layers.append((str(m), os.path.join(instance, "mods", str(m)), "out-of-view"))

    seen_dirs = set()
    for layer, folder, view in layers:
        if folder.lower() in seen_dirs:
            continue
        seen_dirs.add(folder.lower())
        path = _folder_plugins(folder).get(key)
        if path:
            out["rows"].append({"layer": layer, "path": path, "sha256": sha256_file(Path(path))})
            out["views"].append(view)
    if not modlist_read:
        # The modlist is what puts the folders outside the view in priority order. Without it they are
        # not layers, but the copies they hold are real and the reader is told where they are.
        mods_dir = os.path.join(instance, "mods")
        try:
            folders = sorted((e.path for e in os.scandir(mods_dir) if e.is_dir()), key=str.lower)
        except OSError:
            folders = []
        for folder in folders:
            if folder.lower() in seen_dirs:
                continue
            path = _folder_plugins(folder).get(key)
            if path:
                out["rows"].append({"layer": "(not in modlist.txt)", "path": path,
                                    "sha256": sha256_file(Path(path))})
                out["views"].append("unknown")
    return out


def provides(name: str, scope_plugins: dict, resolve=None) -> str:
    """Which layer provides this plugin file, derived from the SAME walk as `requested_plugin`
    (`plugin_layers`): `in-view` when the measured configuration lists the name or a layer 1–3 provides
    it, `out-of-view` when only a folder outside the view does — a real file whose assets are certainly
    not staged — and `missing`, reserved for a name NO folder provides at all."""
    if name.lower() in scope_plugins:
        return "in-view"
    res = resolve(name) if resolve else None
    views = (res or {}).get("views") or []
    if not views:
        return "missing"
    # Precedence order: the first row is the copy a probe would read. A row the modlist could not rank
    # is still a real file outside the view, so it answers out-of-view rather than missing.
    return "in-view" if views[0] == "in-view" else "out-of-view"


def read_candidate(p: Path) -> dict | None:
    doc = load_json(p)
    return doc if isinstance(doc, dict) else None


def requested_plugin(name, scope_plugins: dict, resolve=None) -> dict:
    """One row of D1's requested plugin list.

    A name the MEASURED scope lists keeps that scope's path and hash (`hashSource: "scope"`). A name it
    does not list — a supplied plugin, or one the profile does not load — is resolved at plan time by
    the probes' own precedence (`plugin_layers`), and only when every layer they walk could be walked:
    `--instance` and `--game` for the layers above the profile's other folders, and `--profile` for
    those folders themselves when the name is found nowhere else. The row then carries the
    winning copy's path and hash with `hashSource: "instance:<layer>"`, and `providedBy` lists EVERY
    layer that provides the name, in precedence order, each copy hashed, so a reader sees whether an
    ambiguity exists and whether it matters. Nothing is ever guessed: where the resolution cannot be
    reproduced from the plan's inputs the row stays `unstated` with a null hash and says why."""
    plain = Path(str(name)).name
    scoped = scope_plugins.get(plain.lower())
    if scoped is not None:
        sha = scoped.get("sha256") if is_sha256(scoped.get("sha256")) else None
        row = {"name": str(name), "sha256": sha, "path": scoped.get("path"),
               "hashSource": "scope" if sha else "unstated"}
        if not sha:
            row["unstatedReason"] = (f"the measured scope lists {plain} with no 64-hex sha256, so the "
                                     f"plan states no content for it")
        return row

    row = {"name": str(name), "sha256": None, "path": None, "hashSource": "unstated"}
    res = resolve(plain) if resolve else None
    if res is None:
        row["unstatedReason"] = (f"no --instance was given, so the plan cannot resolve {plain} the way "
                                 f"the probes resolve it")
        return row
    rows, views = res["rows"], res["views"]
    win = next((i for i, v in enumerate(views) if v != "unknown"), None)
    if not res.get("gameWalked"):
        # The probes always walk the game's Data folder, and it outranks every mod folder outside the
        # view. A walk that skipped it cannot say which copy they would read, whichever layer this
        # partial walk found one in — so the plan states the missing flag rather than a guess.
        row["unstatedReason"] = (
            f"no --game was given, so the game's Data folder — a layer the probes always walk, above the "
            f"profile's mod folders outside this configuration's view — was not walked, and the copy of "
            f"{plain} the probes would read cannot be named")
    elif win is not None and is_sha256(rows[win].get("sha256")):
        row.update({"sha256": rows[win]["sha256"], "path": rows[win]["path"],
                    "hashSource": f"instance:{rows[win]['layer']}"})
    elif win is not None:
        row["unstatedReason"] = (f"the copy of {plain} at {rows[win]['path']} would not hash, so the plan "
                                 f"states no content for it")
    elif rows and not res.get("profileNamed"):
        row["unstatedReason"] = (
            f"no --profile was given, so the profile's mod folders outside this configuration's view "
            f"cannot be listed, let alone ranked: the copies of {plain} below sit in folders whose "
            f"priority the plan cannot establish")
    elif rows:
        row["unstatedReason"] = (
            f"{res['modlistPath'] or 'modlist.txt'} would not read, so the mod folders outside this "
            f"configuration's view have no priority order: the probes would refuse the order rather than "
            f"pick one of the copies of {plain} listed below, and so does this plan")
    else:
        row["unstatedReason"] = (f"no layer provides {plain}: not the overwrite folder, not a mod folder "
                                 f"of this configuration's view, not the game's Data folder, and not one "
                                 f"of the profile's other mod folders")
    if rows:
        row["providedBy"] = [dict(r) for r in rows]
    return row


def requested_providers(scope_providers: list[dict], view_mods: list[str] | None) -> list[dict]:
    """D1: the asset providers an arm's configuration reads, in `HarnessOrderCli.Resolve`'s order — the
    overwrite layer, the view's mod folders, then the game's Data folder.

    `view_mods` is None when the candidate declares no `Mods`: the probe then reads the scope's view, and the
    scope's providers are returned as recorded. A declared list — an EMPTY one included — replaces the
    scope's `mod` rows. The other kinds are kept from the scope: the overwrite layer and the Data folder come
    from the instance and the game path, not from the candidate, so every arm resolved under one instance
    reads the same ones, and the scope recorded which of them exist."""
    rows = [dict(v) for v in scope_providers]
    if view_mods is None:
        return rows

    def kind(v):
        return str(v.get("kind") or "").lower()
    return ([v for v in rows if kind(v) == "overwrite"]
            + [{"name": m, "kind": "mod"} for m in view_mods]
            + [v for v in rows if kind(v) not in ("overwrite", "mod")])


def cmd_plan(a) -> int:
    run = Path(a.out)
    registry_path = Path(a.registry)
    scope_path = Path(a.scope) if a.scope else None
    cdir = Path(a.candidates)

    try:
        reg = RegistryView(registry_path)
    except Exception as ex:  # a bad registry is a refused input, never a narrowed checklist
        return refuse(str(ex))

    scope = load_json(scope_path) if scope_path else None
    if scope_path and scope is None:
        return refuse(f"--scope {scope_path}: not readable as JSON. Applicability would be unknown "
                      f"for every check; run scopeprobe first or say so explicitly.")
    scope_sha = sha256_file(scope_path) if scope_path else None

    if not cdir.is_dir():
        return refuse(f"--candidates {cdir}: not a directory")
    files = sorted(cdir.glob("candidate-*.json"))
    if not files:
        return refuse(f"--candidates {cdir}: no candidate-*.json")
    base = [f for f in files if f.stem.endswith("baseline")]
    if len(base) != 1:
        return refuse(f"--candidates {cdir}: expected exactly one candidate-*-baseline.json, found {len(base)}")
    baseline = base[0]

    alts = [f for f in files if f != baseline]
    if a.alt:
        wanted = {Path(x).name for x in a.alt}
        missing = wanted - {f.name for f in alts}
        if missing:
            return refuse("--alt names candidate files that are not in --candidates: " + ", ".join(sorted(missing)))
        alts = [f for f in alts if f.name in wanted]
    # A-4: --max-candidates HOLDS ARMS BACK rather than deleting them from the plan. A held-back
    # arm stays visible, its per-arm checks are skipped-budget, and the count reaches `unassessed`.
    held_back = set()
    if a.max_candidates is not None:
        for f in alts[a.max_candidates:]:
            held_back.add(f.name)

    supplied = []
    for spec in (a.supplied or []):
        # `=` is the separator: `:` collided with a Windows drive letter and declared a plugin file
        # called "...Patch.esp:S". `:` still works when the file part carries none of its own.
        head, sep, tail = spec.partition("=")
        if not sep:
            head, sep, tail = spec.rpartition(":")
            if ":" in tail:
                head, tail = "", ""
        if not head or not tail.lower().endswith(".json"):
            return refuse(f"--supplied {spec!r}: expected PLUGIN[,PLUGIN...]=candidate-NN.json")
        cand = cdir / Path(tail).name if not Path(tail).is_absolute() else Path(tail)
        if not cand.exists():
            return refuse(f"--supplied {spec!r}: candidate file {cand} does not exist")
        supplied.append(([p.strip() for p in head.split(",") if p.strip()], cand))

    scope_plugins = {}
    cfg = (scope or {}).get("configuration") or (scope or {}).get("Configuration") or {}
    for p in cfg.get("plugins", cfg.get("Plugins", []) or []):
        scope_plugins[str(p.get("name", p.get("Name", ""))).lower()] = {
            "name": p.get("name", p.get("Name")), "path": p.get("path", p.get("Path")),
            "sha256": p.get("sha256", p.get("Sha256"))}
    population = cfg.get("population", cfg.get("Population")) or "load-time"
    # D1: the scope's providers and overlays are part of the REQUEST, not decoration. A provider list
    # the plan never recorded cannot be compared to what a child says it read.
    scope_providers = [{"name": v.get("name", v.get("Name")), "kind": v.get("kind", v.get("Kind"))}
                       for v in (cfg.get("providers", cfg.get("Providers", [])) or [])]
    scope_overlays = [{"kind": o.get("kind", o.get("Kind")), "path": o.get("path", o.get("Path")),
                       "sha256": o.get("sha256", o.get("Sha256"))}
                      for o in (cfg.get("overlays", cfg.get("Overlays", [])) or [])]

    states = [s.strip() for s in (a.states or "baseline").split(",") if s.strip()] or ["baseline"]

    arms = []

    def add_arm(kind: str, cand: Path, index: int, declared: list[str] | None = None):
        doc = read_candidate(cand) or {}
        stem = cand.stem
        aid = arm_id(kind, stem, index)
        plugins = list(doc.get("Plugins") or doc.get("plugins") or [])
        # D1: the VIEW this arm's plugins resolve through — the candidate's own mod list when it has
        # one, else the scope's providers of kind `mod` in the order the probe recorded them. The same
        # walk answers the request rows and `provides`, so a plan cannot say one thing in two places.
        # `cand.Mods ?? comp.EnabledMods` (HarnessOrderCli.Resolve) coalesces on NULL: a candidate that
        # carries the key with an EMPTY list is describing an empty view, not asking for the profile's.
        declared_view = doc.get("Mods", doc.get("mods"))
        own_view = None
        if isinstance(declared_view, list):
            view_mods = own_view = [str(m) for m in declared_view if str(m).strip()]
        else:
            view_mods = [str(v.get("name")) for v in scope_providers
                         if str(v.get("kind") or "").lower() == "mod" and v.get("name")]

        def resolve(plain: str, _view=tuple(view_mods)):
            if not a.instance:
                return None
            return plugin_layers(plain, _view, instance=a.instance, profile=a.profile, game=a.game)

        payload_missing, out_of_view = [], []
        for name in (declared or []):
            where = provides(Path(name).name, scope_plugins, resolve)
            if where == "in-view":
                continue
            if where == "out-of-view":
                out_of_view.append(Path(name).name)
            else:
                payload_missing.append(Path(name).name)
        rec = {
            "armId": aid, "kind": kind, "candidateFile": cand.name,
            "candidatePath": str(cand), "candidateSha256": sha256_file(cand),
            "plugins": plugins, "mods": doc.get("Mods") or doc.get("mods"),
            "changes": list(doc.get("Changes") or doc.get("changes") or []),
            "provenance": doc.get("Provenance") or doc.get("provenance") or {},
            "loadedInGame": None,
            "loadedInGameNote": "offline hypothetical configuration; not verified loaded in game",
            "expected": {"population": population, "states": states},
            "heldBack": cand.name in held_back,
            # D1: the frozen REQUEST for this arm — the plugin names in the candidate file's order,
            # each with the hash the scope states for it, or the hash of the copy the probes would read
            # (resolved through this arm's view when `--instance` was given), or an explicit `unstated`
            # saying why neither could be stated; the scope's overlays; the providers THIS arm's view
            # reads (`requested_providers`), never the scope's for an arm that declares a different
            # view; and the population and states the plan selected. Every accepted receipt on this arm
            # is bound to THIS, not to whichever receipt happened to arrive first.
            "requested": {
                "plugins": [requested_plugin(n, scope_plugins, resolve) for n in plugins],
                "overlays": [dict(o) for o in scope_overlays],
                "providers": requested_providers(scope_providers, own_view),
                "population": population,
                "states": list(states),
            },
        }
        if kind == "supplied-combination":
            rec["supplied"] = {"declaredFiles": declared or [], "payloadMissing": payload_missing,
                               "outOfView": out_of_view}
            note = ("an ESP-only arm: the supplied plugin was placed in the order, but its meshes, "
                    "scripts and installer choices were NOT staged and are not part of this evaluation")
            if out_of_view:
                note += ("; " + ", ".join(out_of_view) + " is provided only by a mod folder that is "
                         "not in the profile's view, so its assets are certainly not staged")
            rec["payload"] = {"espOnly": True, "staged": [], "note": note}
        else:
            rec["payload"] = {"espOnly": False, "staged": [],
                              "note": "the candidate reorders plugins already present in the configuration"}
        arms.append(rec)

    add_arm("baseline", baseline, 0)
    for i, f in enumerate(alts, start=1):
        add_arm("candidate", f, i)
    for i, (declared, cand) in enumerate(supplied, start=1):
        add_arm("supplied-combination", cand, i, declared)

    manifest = load_json(cdir / "candidates.json") or {}
    budget_block = manifest.get("Budget") if isinstance(manifest, dict) else None

    known = {c["id"] for c in reg.checks}
    by_exe: dict[str, set[str]] = {}
    for c in reg.checks:
        e = (c.get("adapter") or {}).get("exe")
        if e:
            by_exe.setdefault(e, set()).add(c["id"])

    # A-3: --select takes check ids; an exe name is accepted as a convenience and expands to every
    # check whose adapter runs that exe, so `--select navownprobe,overlapprobe,contactprobe` is the
    # packet's case. The plan records the RESOLVED ids, never the shorthand.
    selected = None
    select_raw = None
    implied = {}
    if a.select:
        select_raw = [t.strip() for t in ",".join(a.select).split(",") if t.strip()]
        selected = set()
        unresolved = []
        for tok in select_raw:
            if tok in known:
                selected.add(tok)
            elif tok in by_exe:
                selected |= by_exe[tok]
            else:
                unresolved.append(tok)
        if unresolved:
            return refuse("--select names neither a registry check id nor an adapter exe: "
                          + ", ".join(sorted(unresolved)))
        # A selection implies every prerequisite it transitively needs — otherwise every narrowing
        # would block on the checks it depends on. The implication is recorded, not silent.
        by_id = {c["id"]: c for c in reg.checks}
        implied = {}
        frontier = list(selected)
        while frontier:
            cid = frontier.pop()
            for pre in (by_id.get(cid) or {}).get("prerequisites") or []:
                if pre not in selected:
                    selected.add(pre)
                    implied[pre] = f"implied-by:{cid}"
                    frontier.append(pre)
    # An opt-in check (registry `optIn: true`) is planned only when the caller names it: the controller never
    # launches the game, and a live check is something the caller asks for, not a default. Opting in makes it
    # required for this run, so an evaluation that asked for it cannot finalize complete without it.
    opted = set()
    for tok in [t.strip() for t in ",".join(a.opt_in or []).split(",") if t.strip()]:
        chk = next((c for c in reg.checks if c["id"] == tok), None)
        if chk is None:
            return refuse(f"--opt-in {tok!r}: the registry defines no such check")
        if not chk.get("optIn"):
            return refuse(f"--opt-in {tok!r}: that check is not opt-in (it is planned by default)")
        opted.add(tok)
    excluded = {}
    for spec in (a.exclude or []):
        cid, _, reason = spec.partition("=")
        if not cid.strip() or not reason.strip():
            return refuse(f"--exclude {spec!r}: expected CHECK=reason (a reason is not optional)")
        excluded[cid.strip()] = reason.strip()
    unknown_excl = set(excluded) - known
    if unknown_excl:
        return refuse("--exclude names check ids the registry does not define: "
                      + ", ".join(sorted(unknown_excl)))

    probe_cmds = {}
    for spec in (a.probe_cmd or []):
        name, _, prefix = spec.partition("=")
        if not name.strip() or not prefix.strip():
            return refuse(f"--probe-cmd {spec!r}: expected NAME=<argv prefix>")
        probe_cmds[name.strip()] = prefix.strip()

    harness = Path(a.harness).resolve()
    options = {
        "instance": a.instance, "profile": a.profile, "game": a.game,
        "harness": str(harness), "probeArgs": a.probe_args or "",
        "probeCmd": probe_cmds, "diffTolerance": a.diff_tolerance,
        "noReverse": bool(a.no_reverse), "population": population,
    }

    for check in reg.checks:
        for tok in (check.get("adapter") or {}).get("args") or []:
            for name in re.findall(r"\{([A-Za-z0-9_]+)\}", str(tok)):
                if name not in PLACEHOLDERS:
                    return refuse(f"registry check {check['id']}: adapter.args uses the placeholder "
                                  f"{{{name}}}, which this controller cannot fill. Known placeholders: "
                                  + ", ".join(sorted(PLACEHOLDERS))
                                  + ". A placeholder is never passed through to a probe literally.")

    app_map = {}
    try:
        app_map = reg.applicability_map(scope) or {}
    except Exception as ex:
        return refuse(f"the registry's applicability could not be derived from this scope: {ex}")

    checks = []
    unknown_applicability = []
    ordersweep_probes = []
    touched = {}
    tools = {}
    for check in reg.checks:
        cid = check["id"]
        adapter = dict(check.get("adapter") or {})
        kind = adapter.get("kind", "unsupported")
        row = app_map.get(cid) or {}
        applicability = row.get("applicability", "unknown")
        evidence = row.get("evidence", "the registry returned no applicability for this check")
        if applicability not in ("yes", "no", "unknown"):
            return refuse(f"registry check {cid}: applicability {applicability!r} is not yes/no/unknown")

        want = (check.get("inputs") or {}).get("arms", "all")
        arm_ids = [x["armId"] for x in arms] if want == "all" else ["baseline"]

        exe = adapter.get("exe")
        if exe and exe not in tools:
            tools[exe] = tool_identity(harness, exe)

        # A-2, first match wins. The order matters: a check the runner COULD NOT have run is
        # recorded as unrunnable whether or not the caller selected it, and a caller's narrowing is
        # recorded for optional checks too — `required` decides whether a skip blocks completion,
        # not whether the skip is written down.
        base_status = None
        if applicability == "no":
            base_status = "not-applicable"
        elif check.get("optIn") and cid not in opted:
            base_status = NOT_OPTED_IN
        elif kind in ("unsupported", "mcp-only", "manual", "live-import"):
            base_status = kind

        per_arm = []
        for aid in arm_ids:
            arm = next(x for x in arms if x["armId"] == aid)
            st = base_status
            if st is None:
                if kind == "probe-target" and aid != "baseline":
                    st = "unsupported-for-arm"
                elif want == "baseline" and aid != "baseline":
                    st = "unsupported-for-arm"
                elif arm.get("supplied", {}).get("payloadMissing"):
                    st = "blocked-payload-missing"
                elif cid in excluded:
                    st = "excluded"
                    touched[cid] = "excluded"
                elif selected is not None and cid not in selected:
                    st = "skipped-user"
                    touched[cid] = "not in --select"
                elif arm.get("heldBack"):
                    st = "skipped-budget"
                else:
                    st = "planned"
            elif cid in excluded:
                touched[cid] = f"excluded, but the status is {st} (it could not have run anyway)"
            elif selected is not None and cid not in selected:
                touched[cid] = f"not in --select, but the status is {st} (it could not have run anyway)"
            per_arm.append({"armId": aid, "status": st})

        if base_status is None:
            base_status = "planned" if any(x["status"] == "planned" for x in per_arm) \
                else (per_arm[0]["status"] if per_arm else "planned")

        if any(x["status"] == "planned" for x in per_arm) and kind == "ordersweep-probe" and exe:
            ordersweep_probes.append(exe)

        if applicability == "unknown":
            unknown_applicability.append({"checkId": cid, "evidence": evidence})

        checks.append({
            "checkId": cid, "family": check.get("family"), "title": check.get("title"),
            "required": bool(check.get("required")) or cid in opted,
            **({"optIn": True, "optedIn": cid in opted} if check.get("optIn") else {}),
            "applicability": applicability, "applicabilityEvidence": evidence,
            "prerequisites": list(check.get("prerequisites") or []),
            "inputs": check.get("inputs") or {},
            "evidence": check.get("evidence") or {},
            "adapter": adapter,
            "manualDecisionAllowed": bool(check.get("manualDecisionAllowed")),
            "unsupportedReason": check.get("unsupportedReason"),
            "arms": per_arm,
            "status": base_status,
        })

    states_not_explored = []
    for c in checks:
        if (c["inputs"] or {}).get("states") == "all":
            states_not_explored.append({
                "checkId": c["checkId"], "requires": "all", "selectedStates": states,
                "note": "states beyond the selected ones were not enumerated: this plan carries no "
                        "state census, so what was not explored is unknown in extent, not empty"})

    plan = {
        "schemaVersion": SCHEMA_VERSION,
        "planId": None,
        "requestProviders": REQUEST_PROVIDERS,
        "createdUtc": utcnow(),
        "registry": {"path": str(registry_path), "registryVersion": reg.version, "sha256": reg.sha256},
        "tools": tools,
        "runner": {
            "evalrun.py": sha256_file(HERE / "evalrun.py"),
            "evalregistry.py": sha256_file(HERE / "evalregistry.py"),
            "ordersweep.py": sha256_file(HERE / "ordersweep.py"),
            "runlog.py": sha256_file(HERE.parent / "scripts" / "runlog.py"),
        },
        "targets": (scope or {}).get("targets", []),
        "scope": {"path": str(scope_path) if scope_path else None, "sha256": scope_sha,
                  "complete": (scope or {}).get("complete"),
                  "unmeasured": (scope or {}).get("unmeasured", []),
                  "recordTypes": list(((scope or {}).get("recordTypes") or {}).keys()),
                  "walkFailures": (scope or {}).get("walkFailures", [])},
        "configuration": {"fingerprint": cfg.get("fingerprint", cfg.get("Fingerprint")),
                          "source": cfg.get("source", cfg.get("Source")),
                          "population": population,
                          "plugins": list(scope_plugins.values()),
                          "providers": scope_providers,
                          "overlays": scope_overlays},
        "candidatesDir": str(cdir),
        "arms": arms,
        "states": {"selected": states, "notExplored": states_not_explored},
        "checks": checks,
        "options": options,
        "ordersweepProbes": sorted(set(ordersweep_probes)),
        "budgets": {
            "minutes": a.budget_minutes,
            "maxCandidates": a.max_candidates,
            "candidatesHeldBack": {"byMaxCandidates": sorted(held_back),
                                   "count": len(held_back),
                                   "fromManifestBudget": budget_block},
            "statesNotExplored": states_not_explored,
        },
        "explicitDecisions": {
            "select": sorted(selected) if selected is not None else None,
            "selectRaw": select_raw,
            "selectImplied": implied,
            "exclude": excluded,
            **({"optIn": sorted(opted)} if opted else {}),
            "touched": touched,
            "note": "--select narrows what RUNS, never what is PLANNED: a check that was not selected "
                    "is skipped-user and stays visible, and a required one keeps the evaluation "
                    "incomplete. An exe name in --select expands to every check that runs it.",
        },
    }
    plan["planId"] = sha256_text(canonical({k: v for k, v in plan.items() if k != "planId"}))[:12]

    run.mkdir(parents=True, exist_ok=True)
    set_run_store(run)
    with Run("evalrun-plan", stage_source="counted") as r:
        r.feature("checks", len(checks), "checks")
        r.feature("arms", len(arms), "arms")
        r.feature("probes", len(plan["ordersweepProbes"]), "probes")
        r.stage("plan", total=1, unit="plans")
        write_json(run / "plan.json", plan)
        (run / "plan.sha256").write_text(sha256_file(run / "plan.json") + "\n", encoding="utf-8")
        r.advance()

    ledger = {"schemaVersion": SCHEMA_VERSION, "planId": plan["planId"],
              "registrySha256": reg.sha256, "runIds": [], "entries": {}}
    write_json(run / "ledger.json", ledger)
    (run / "journal.jsonl").touch()

    print(f"plan {plan['planId']}: {len(checks)} checks, {len(arms)} arms, states {','.join(states)}")
    for c in checks:
        if c["status"] != "planned":
            print(f"   {c['checkId']:38} {c['status']}")
    if unknown_applicability:
        print(f"   {len(unknown_applicability)} check(s) of unknown applicability — they keep the "
              f"evaluation incomplete until measured")
    print(f"written: {run / 'plan.json'}")
    return EXIT_OK


# -------------------------------------------------------------------------- run store (D9) --

def set_run_store(run: Path) -> None:
    """A run's timing history stays with the run unless the caller said otherwise."""
    if not (os.environ.get("HOUSECARL_RUNLOG_DIR") or "").strip():
        os.environ["HOUSECARL_RUNLOG_DIR"] = str((Path(run) / "runlog").resolve())


def load_plan(run: Path) -> tuple[dict | None, str | None]:
    """The plan, or the reason it is refused, as `<cause>: <detail>`. Immutability and the schema
    version are checked here and nowhere else."""
    p = Path(run) / "plan.json"
    if not p.exists():
        return None, f"plan-missing: {p} does not exist: this is not an evalrun RUN directory"
    recorded = (Path(run) / "plan.sha256")
    if not recorded.exists():
        return None, f"plan-changed: {recorded} is missing, so the plan's immutability cannot be checked"
    if sha256_file(p) != recorded.read_text(encoding="utf-8").strip():
        return None, "plan-changed: plan.json no longer hashes to plan.sha256"
    doc = load_json(p)
    if not isinstance(doc, dict):
        return None, f"plan-unreadable: {p} is not readable as JSON"
    # A-1: a plan written before the evidence-chain schema carries no per-arm `requested` identity, so
    # nothing in it can bind a receipt to what was asked for. Refused by name, never read anyway.
    if doc.get("schemaVersion") != SCHEMA_VERSION:
        return None, (f"plan-schema-unsupported: plan.json declares schemaVersion "
                      f"{doc.get('schemaVersion')!r}; this controller reads {SCHEMA_VERSION}. A schema-1 "
                      f"plan carries no per-arm requested identity, so an accepted receipt could not be "
                      f"bound to the configuration the arm asked for. Next action: plan again from the "
                      f"same registry, scope and candidates.")
    return doc, None


def request_refusal(plan: dict) -> str | None:
    """D1: a plan frozen before `requestProviders` carries the scope's providers on EVERY arm. A receipt newly
    bound to it on an arm whose own view differs is bound to a configuration that arm never read: an honest
    import is refused `identity-mismatch`, and one stating the scope's view is accepted. The plan cannot be
    re-read under the new meaning either — `arms[].mods` does not keep an empty `Mods` apart from an absent
    one — so the verbs that bind new receipts (`run`, `resume`, `import-results`) refuse it by name. The
    verbs that only read what was already bound (`finalize`, `bundle`, `status`, `verify-bundle`) do not."""
    if plan.get("requestProviders") == REQUEST_PROVIDERS:
        return None
    return (f"plan-request-outdated: plan.json declares requestProviders {plan.get('requestProviders')!r}, "
            f"not {REQUEST_PROVIDERS!r}: it froze the scope's providers onto every arm, so a receipt bound to "
            f"it now could be bound to a view that arm never read. Next action: plan again from the same "
            f"registry, scope and candidates.")


def refusal_cause(why: str) -> str:
    """The cause word out of a `<cause>: <detail>` refusal, for the disposition's `refused` field."""
    head = str(why or "").split(":", 1)[0].strip()
    return head if re.fullmatch(r"[a-z][a-z-]*", head or "") else "plan-changed"


def premise_refusal(plan: dict) -> str | None:
    """D5: the registry and the scope are PREMISES of a frozen plan, not inputs a verb may re-read.
    A checklist or a measured scope that changed after the plan froze changes which checks apply, and
    that is a new plan — never reuse of the old applicability."""
    reg = Path(plan["registry"]["path"])
    nxt = "Next action: re-run scopeprobe and plan again."
    if not reg.exists():
        return (f"registry-missing: the plan's registry {reg} does not exist, so the checklist this run "
                f"was derived from cannot be re-read. {nxt}")
    if sha256_file(reg) != plan["registry"]["sha256"]:
        return (f"registry-changed: {reg} now hashes {str(sha256_file(reg))[:12]}, the plan recorded "
                f"{plan['registry']['sha256'][:12]}. A checklist cannot change under a frozen plan. {nxt}")
    spath = (plan.get("scope") or {}).get("path")
    if not spath:
        return None  # a plan made without --scope has no scope premise
    sp = Path(spath)
    if not sp.exists():
        return (f"scope-missing: the plan's scope {sp} does not exist, so the applicability this plan "
                f"froze cannot be re-derived. {nxt}")
    got = sha256_file(sp)
    if got != plan["scope"]["sha256"]:
        return (f"scope-changed: {sp} now hashes {str(got)[:12]}, the plan recorded "
                f"{str(plan['scope']['sha256'])[:12]}. A scope change changes the checklist. {nxt}")
    return None


# ------------------------------------------------------------------- ledger and journal (D5) --

class Ledger:
    def __init__(self, run: Path, plan: dict):
        self.run = Path(run)
        self.plan = plan
        self.path = self.run / "ledger.json"
        self.journal = self.run / "journal.jsonl"
        self.doc = load_json(self.path)
        self.run_id = None

    @staticmethod
    def key(check_id: str, arm_id: str, part: str | None) -> str:
        return f"{check_id}__{arm_id}" + (f"__{part}" if part else "")

    def refusal(self) -> str | None:
        if self.doc is None:
            return (f"{self.path} is missing or corrupt. It is the run's record of what happened; "
                    f"rebuild it from the journal with `resume --rebuild-ledger` rather than guessing.")
        if self.doc.get("planId") != self.plan["planId"]:
            return (f"foreign-run: {self.path} belongs to plan {self.doc.get('planId')}, "
                    f"not {self.plan['planId']}")
        return None

    def rebuild(self) -> None:
        """The journal is the recovery source: the ledger is a view of it PLUS the receipts.

        The journal line records the transition (from, to, cause, receiptId); it has never carried the
        per-entry facts a transition also writes — `detector`, `validation`, and an import's `imported` /
        `importOrigin` block. Those live in the receipt, which is why this is a view of both. Replaying the
        journal alone dropped them, and `finalize` reads `detector` from the LEDGER while reading everything
        else from the receipt, so every rebuilt entry then finalized as `detector-undetermined` — a check
        with accepted evidence on disk reported as missing (Piranha, 2026-09-17). A fact is restored only
        from the receipt this entry actually names, so a stale or foreign receipt file cannot donate one."""
        self.doc = {"schemaVersion": SCHEMA_VERSION, "planId": self.plan["planId"],
                    "registrySha256": self.plan["registry"]["sha256"], "runIds": [], "entries": {}}
        if self.journal.exists():
            for line in self.journal.read_text(encoding="utf-8", errors="replace").splitlines():
                line = line.strip()
                if not line:
                    continue
                try:
                    ev = json.loads(line)
                except ValueError:
                    continue
                k = self.key(ev.get("checkId", "?"), ev.get("armId", "?"), ev.get("part"))
                self.doc["entries"][k] = {
                    "checkId": ev.get("checkId"), "armId": ev.get("armId"), "part": ev.get("part"),
                    "status": ev.get("to"), "cause": ev.get("cause"),
                    "detail": ev.get("detail"), "nextAction": ev.get("nextAction"),
                    "receiptId": ev.get("receiptId"), "runId": ev.get("runId"),
                    "receiptPath": f"receipts/{k}.json", "updatedUtc": ev.get("t")}
        for k, e in self.doc["entries"].items():
            e.update(self.receipt_facts(k, e))
        self.save()

    def receipt_facts(self, key: str, entry: dict) -> dict:
        """The per-entry facts the journal does not carry, read from the receipt this entry NAMES.

        Refused unless the receipt on disk is this plan's, this (check, arm, part)'s, and the very
        `receiptId` the journal recorded — a rebuild recovers what was written, it never adopts whatever
        file happens to sit at the path. An entry that names no receipt, or whose receipt does not match,
        gets nothing back and keeps the journal's own account of itself."""
        rid = entry.get("receiptId")
        if not rid:
            return {}
        r = load_json(Path(self.run) / "receipts" / f"{key}.json")
        if not isinstance(r, dict) or r.get("receiptId") != rid:
            return {}
        if (r.get("planId") != self.plan["planId"] or r.get("checkId") != entry.get("checkId")
                or r.get("armId") != entry.get("armId") or r.get("part") != entry.get("part")):
            return {}
        out: dict = {}
        detector = (r.get("result") or {}).get("detector")
        if detector is not None:
            out["detector"] = detector
            out["result"] = {"detector": detector, "execution": entry.get("status")}
        if isinstance(r.get("validation"), dict):
            out["validation"] = {"accepted": bool(r["validation"].get("accepted")),
                                 "rejections": r["validation"].get("rejections") or []}
        if isinstance(r.get("imported"), dict):
            out["imported"] = True
            out["importOrigin"] = r["imported"].get("origin")
        return out

    def open_invocation(self, command: str, run_id: str | None, parent: str | None) -> None:
        self.run_id = run_id
        self.doc.setdefault("runIds", []).append(
            {"runId": run_id, "parentRunId": parent, "command": command, "startedUtc": utcnow()})
        self.save()

    def entry(self, check_id: str, arm_id: str, part: str | None) -> dict | None:
        return self.doc["entries"].get(self.key(check_id, arm_id, part))

    def transition(self, check_id: str, arm_id: str, part: str | None, to: str, *,
                   cause: str | None = None, receipt_id: str | None = None,
                   detector: str | None = None, extra: dict | None = None,
                   rejections: list | None = None, detail: str | None = None,
                   next_action: str | None = None) -> None:
        k = self.key(check_id, arm_id, part)
        prev = self.doc["entries"].get(k)
        frm = prev.get("status") if prev else None
        rec = {"checkId": check_id, "armId": arm_id, "part": part, "status": to,
               "cause": cause, "receiptId": receipt_id, "detector": detector,
               "detail": detail, "nextAction": next_action,
               "validation": {"accepted": not (rejections or []), "rejections": rejections or []},
               "result": {"detector": detector, "execution": to},
               "receiptPath": f"receipts/{k}.json", "runId": self.run_id, "updatedUtc": utcnow()}
        if prev:
            for keep in ("reusedFromRunId", "derivedFrom", "imported", "importOrigin"):
                if keep in prev and (extra or {}).get(keep) is None:
                    rec[keep] = prev[keep]
        rec.update(extra or {})
        self.doc["entries"][k] = rec
        with open(self.journal, "a", encoding="utf-8") as f:
            f.write(canonical({"t": utcnow(), "checkId": check_id, "armId": arm_id, "part": part,
                               "from": frm, "to": to, "cause": cause, "receiptId": receipt_id,
                               "detail": detail, "nextAction": next_action,
                               "runId": self.run_id}) + "\n")
        self.save()

    def save(self) -> None:
        write_json(self.path, self.doc)

    def heal_interrupted(self) -> int:
        """A `running` entry with no terminal line is an INTERRUPTED one, never a completed one."""
        n = 0
        for k, e in list(self.doc["entries"].items()):
            if e.get("status") == "running":
                self.transition(e["checkId"], e["armId"], e.get("part"), "interrupted",
                                cause="the process did not write a terminal transition")
                n += 1
        return n


# ------------------------------------------------------------------ receipts and validation --

class Validator:
    """Every rejection cause in the closed vocabulary is decided here, from evidence on disk."""

    def __init__(self, run: Path, plan: dict):
        self.run = Path(run)
        self.plan = plan
        # (armId, population, state) -> fingerprint: a fingerprint is keyed WITH the probe's own
        # population and state, so two probes of one arm that judge different populations produce
        # different fingerprints over identical inputs. Comparing them across an arm reports a
        # mismatch that is not one.
        self.arm_fingerprints: dict[tuple, str] = {}
        self.arm_plugin_hashes: dict[str, dict[str, str]] = {}
        self.seen_keys: set[str] = set()

    def reference_arm(self, arm: dict, part: str | None) -> dict:
        """Which arm's frozen REQUEST an emit is compared against. A `reverse` part is the probe run on
        the BASELINE order with the candidate as `--base`, so its own configuration is the baseline's
        and the baseline's request is its reference — validated against it, never exempted from it."""
        if part == "reverse":
            base = next((a for a in self.plan.get("arms", []) if a.get("kind") == "baseline"), None)
            if base is not None:
                return base
        return arm

    #: scopeprobe/Program.cs's own wording for a named target the resolved configuration lacks.
    SCOPE_ABSENT_TARGET = "'{name}' is not in the resolved configuration ("

    def scope_omission(self, arm: dict, part: str | None, doc) -> dict:
        """Is a scopeprobe exit 1 on this arm fully explained by LEGAL target omissions?

        A plan names its targets once, and every arm is told all of them. An add/remove arm's frozen
        request leaves some out by design, and scopeprobe then reports each as a walk failure. That is
        not a loadability failure, but it is only treated as an omission when ALL of these hold:
          - the resolved configuration is exactly the arm's frozen request (names, in order), so the
            order that loaded is the order the plan approved;
          - every walk failure is the probe's absent-target sentence for a plan target the request
            does NOT load, and that target is really absent from the resolved configuration;
          - every plan target the request DOES load was measured.
        A requested target the configuration lacks, any other walk failure, or an unreadable document
        is not an omission, and `why` says which rule failed."""
        ref = self.reference_arm(arm, part)
        out = {"legal": False, "why": "", "omitted": [], "measured": [], "coverage": None}
        if not isinstance(doc, dict):
            out["why"] = "the scope JSON could not be read, so its walk failures cannot be classified"
            return out
        failures = [str(f) for f in (doc.get("walkFailures") or [])]
        cfg = doc.get("configuration") or doc.get("Configuration") or {}
        resolved = [str(p.get("Name", p.get("name")) or "") for p in (cfg.get("plugins") or cfg.get("Plugins") or [])
                    if isinstance(p, dict)]
        req_rows = (ref.get("requested") or {}).get("plugins")
        requested = ([str(p.get("name") or "") for p in req_rows if isinstance(p, dict)]
                     if isinstance(req_rows, list) else [str(p) for p in (ref.get("plugins") or [])])
        targets = [str(t.get("name")) for t in (self.plan.get("targets") or [])
                   if isinstance(t, dict) and t.get("name")]
        req_low = {n.lower() for n in requested}
        res_low = {n.lower() for n in resolved}
        omitted = [t for t in targets if t.lower() not in req_low]
        loaded = [t for t in targets if t.lower() in req_low]
        measured = [str(t.get("name") or "") for t in (doc.get("targets") or []) if isinstance(t, dict)]
        out["omitted"], out["measured"] = omitted, measured
        unexplained = [f for f in failures
                       if not any(f.startswith(self.SCOPE_ABSENT_TARGET.format(name=t))
                                  and t.lower() not in res_low for t in omitted)]
        if unexplained:
            out["why"] = ("walk failure(s) that are not a target this arm's request leaves out: "
                          + "; ".join(unexplained))
        elif not failures:
            # Every failure above is explained by an omitted target, so a non-empty list implies one.
            out["why"] = "the probe named no walk failure, so its incompleteness is not a target this arm leaves out"
        elif not resolved or [n.lower() for n in resolved] != [n.lower() for n in requested]:
            out["why"] = (f"the resolved configuration ({len(resolved)} plugins) is not this arm's frozen "
                          f"request ({len(requested)} plugins), so what loaded is not the order the plan approved")
        elif sorted(n.lower() for n in measured) != sorted(t.lower() for t in loaded):
            out["why"] = ("a plan target this arm's request loads was not measured: "
                          + ", ".join(t for t in loaded if t.lower() not in {m.lower() for m in measured}))
        else:
            out["legal"] = True
            it = "it" if len(omitted) == 1 else "them"
            out["coverage"] = {
                "Population": "target footprints", "Judged": len(measured),
                "Unjudgeable": {"not-loaded-by-this-arm": len(omitted)},
                "Note": (f"the order resolved to this arm's frozen request ({len(resolved)} plugins); that is "
                         f"this check's loadability answer. Not measured on this arm: "
                         + ", ".join(omitted) + f" - the arm's request does not load {it}, so no footprint "
                         f"of {it} was requested or measured here, and nothing in this receipt describes {it}.")}
        return out

    # -- readers ---------------------------------------------------------------------------

    @staticmethod
    def emit_identity(doc: dict) -> dict | None:
        cfg = doc.get("Configuration") or doc.get("configuration")
        if not cfg:
            return None
        return {"fingerprint": cfg.get("Fingerprint", cfg.get("fingerprint", "")),
                "source": cfg.get("Source", cfg.get("source", "")),
                "schemaVersion": cfg.get("SchemaVersion", cfg.get("schemaVersion")),
                "plugins": [{"name": p.get("Name", p.get("name")), "path": p.get("Path", p.get("path")),
                             "sha256": p.get("Sha256", p.get("sha256"))}
                            for p in cfg.get("Plugins", cfg.get("plugins", []) or [])],
                "providers": [{"name": v.get("Name", v.get("name")), "kind": v.get("Kind", v.get("kind"))}
                              for v in cfg.get("Providers", cfg.get("providers", []) or [])],
                "overlays": [{"kind": o.get("Kind", o.get("kind")), "path": o.get("Path", o.get("path")),
                              "sha256": o.get("Sha256", o.get("sha256"))}
                             for o in cfg.get("Overlays", cfg.get("overlays", []) or [])],
                "population": cfg.get("Population", cfg.get("population")),
                "state": cfg.get("State", cfg.get("state")),
                "limits": cfg.get("Limits", cfg.get("limits", []) or [])}

    @staticmethod
    def coverage_lines(doc: dict) -> list[dict]:
        return list(doc.get("Coverage") or doc.get("coverage") or [])

    @staticmethod
    def judged(line: dict) -> int:
        return int(line.get("Judged", line.get("judged", 0)) or 0)

    @staticmethod
    def unjudgeable(line: dict) -> int:
        d = line.get("Unjudgeable", line.get("unjudgeable")) or {}
        return sum(int(v or 0) for v in d.values()) if isinstance(d, dict) else 0

    # -- the checks ------------------------------------------------------------------------

    def validate(self, *, check: dict, arm: dict, part: str | None, adapter: dict,
                 exit_code: int | None, log_text: str, outputs: list[Path],
                 pre_hashes: dict[str, str | None], started: float | None) -> dict:
        """Returns {accepted, rejections, detector, findings, coverage, verdict, identity}."""
        rej: list[dict] = []
        identity_limits: list[dict] = []
        out_kind = adapter.get("output") or "conflict-file"
        key = Ledger.key(check["checkId"], arm["armId"], part)
        if key in self.seen_keys:
            rej.append({"cause": "duplicate-receipt",
                        "detail": f"{key} was already receipted in this invocation"})
        self.seen_keys.add(key)

        primary = outputs[0] if outputs else None
        produced = primary is not None and primary.exists()
        # A refusal is a MARKER plus a failure to deliver: exit != 0, or no output. A probe that
        # exited 0 and wrote a valid emit did not refuse, whatever its prose says.
        # Every line of the child's log, the `---- stderr ----` section included: a probe that
        # refuses on stderr refused just as much as one that refuses on stdout.
        marker = REFUSED_RE.search(log_text or "")
        refused = bool(marker) and (exit_code not in (0, None) or not produced)
        order_kind = adapter.get("kind") in ORDER_CONSUMING_KINDS

        # scopeprobe's exit 1 means "written but incomplete". An arm that legally omits a plan target
        # (its frozen request does not load it) always exits 1, so that exit alone says nothing about
        # the order's loadability; `scope_omission` decides whether every walk failure is such an
        # omission and nothing else. Anything it cannot explain stays a rejection, named.
        omission = None
        if (adapter.get("kind") == "scopeprobe" and exit_code == 1 and produced and not refused
                and out_kind == "json"):
            omission = self.scope_omission(arm, part, load_json(primary))

        if exit_code == 2 and refused and not produced and order_kind:
            line = next((ln.strip() for ln in (log_text or "").splitlines() if REFUSED_RE.search(ln)), "")
            rej.append({"cause": "illegal-configuration",
                        "detail": "the child refused the CONFIGURATION itself (exit 2, a refusal "
                                  "marker, no output): the game would not load it as written"
                                  + (f" - the child's refusal: {line[:300]!r}" if line else "")})
        elif refused:
            rej.append({"cause": "refused",
                        "detail": f"refusal marker in the child's log (stderr included): "
                                  f"{marker.group(0).strip()!r}"})
            if exit_code not in (0, None):
                rej.append({"cause": "exit-nonzero", "detail": f"exit code {exit_code}"})
        elif omission is not None and omission["legal"]:
            pass
        elif exit_code not in (0, None):
            rej.append({"cause": "exit-nonzero", "detail": f"exit code {exit_code}"
                        + (f": {omission['why']}" if omission is not None else "")})

        doc = None
        detector = None
        findings = None
        coverage = []
        verdict = None
        identity = None

        for p in outputs:
            if not p.exists():
                rej.append({"cause": "output-missing", "detail": rel_to(self.run, p)})

        if produced and started is not None:
            st = primary.stat()
            pre = pre_hashes.get(str(primary))
            now_hash = sha256_file(primary)
            if st.st_mtime + 2.0 < started:
                rej.append({"cause": "output-stale",
                            "detail": f"{rel_to(self.run, primary)} was last written {iso_of(st.st_mtime)}, "
                                      f"before the child started {iso_of(started)}"})
            elif pre is not None and now_hash == pre:
                rej.append({"cause": "output-stale",
                            "detail": f"{rel_to(self.run, primary)} is byte-identical to the file that "
                                      f"existed before the child started: nothing was produced"})

        if out_kind == "text":
            produced = True  # the log IS the output; `output-missing` does not apply to it
        if produced and out_kind != "text":
            doc = load_json(primary)
            if doc is None or not isinstance(doc, dict):
                rej.append({"cause": "schema-unsupported",
                            "detail": f"{rel_to(self.run, primary)} is not a JSON object"})
            elif out_kind == "conflict-file":
                if "Probe" not in doc or "Conflicts" not in doc:
                    rej.append({"cause": "schema-unsupported",
                                "detail": "not a conflict file: no Probe / Conflicts"})
                else:
                    exe = adapter.get("exe")
                    if exe and str(doc.get("Probe", "")).lower() != str(exe).lower():
                        rej.append({"cause": "wrong-check",
                                    "detail": f"the emit says Probe='{doc.get('Probe')}' but the adapter ran '{exe}'"})
                    identity = self.emit_identity(doc)
                    if identity is None:
                        rej.append({"cause": "identity-missing",
                                    "detail": "the emit carries no Configuration: what it consumed cannot be stated"})
                    else:
                        rej += self._identity_checks(identity, arm, doc, adapter, part, identity_limits)
                    coverage = self.coverage_lines(doc)
                    if not coverage:
                        rej.append({"cause": "coverage-missing",
                                    "detail": "the emit declares no Coverage: what was judged is unknown"})
                    else:
                        first = coverage[0]
                        all_zero = all(self.judged(c) == 0 for c in coverage)
                        if (self.judged(first) == 0 and self.unjudgeable(first) > 0) or all_zero:
                            rej.append({"cause": "extraction-failed",
                                        "detail": f"population '{first.get('Population', first.get('population'))}' "
                                                  f"judged {self.judged(first)} with {self.unjudgeable(first)} "
                                                  f"unjudgeable: zero findings here is zero assessed subjects"})
                    findings = len(doc.get("Conflicts") or [])
            elif adapter.get("kind") == "scopeprobe" and "complete" in doc:
                # D2 defines scope.json's own `complete`; this is the probe's contract, not a guess.
                findings = 0 if doc.get("complete") else None
                if omission is not None and omission["legal"]:
                    # The order resolved to exactly this arm's request, so its loadability is answered.
                    # The footprint of a target the arm does not load was never requested here: it is
                    # stated as unmeasured, never counted as measured.
                    findings = 0
                    coverage = [omission["coverage"]]
            else:
                # A-6: a json / text output is read ONLY where the registry's `adapter.result` says
                # its numbers live. Nothing is inferred from a shape the controller was not told.
                spec = adapter.get("result") or {}
                result_kind = spec.get("kind")
                judged = unjudgeable = None
                for name, target in (("findings", "findings"), ("judged", "judged"),
                                     ("unjudgeable", "unjudgeable")):
                    raw = spec.get(name)
                    if raw is None:
                        continue
                    val, why = resolve_spec(raw, doc, log_text)
                    if val is None:
                        rej.append({"cause": "schema-unsupported",
                                    "detail": f"adapter.result.{name} = {raw!r} did not resolve "
                                              f"against this output: {why}"})
                        continue
                    if target == "findings":
                        findings = val
                    elif target == "judged":
                        judged = val
                    else:
                        unjudgeable = val
                if judged == 0 and (unjudgeable or 0) > 0:
                    rej.append({"cause": "extraction-failed",
                                "detail": f"adapter.result judged 0 with {unjudgeable} unjudgeable: "
                                          f"zero rows here is zero assessed subjects"})
                if judged is not None:
                    coverage = [{"Population": spec.get("unit", "rows"), "Judged": judged,
                                 "Unjudgeable": ({"unjudgeable": unjudgeable} if unjudgeable else {}),
                                 "Note": "from adapter.result"}]

        if out_kind == "text":
            # A text probe's numbers live in its own printed lines; the registry's regex specs say
            # where. Nothing is inferred from a shape this controller was not told about.
            spec = adapter.get("result") or {}
            judged = unjudgeable = None
            for name in ("findings", "judged", "unjudgeable"):
                raw = spec.get(name)
                if raw is None:
                    continue
                val, why = resolve_spec(raw, None, log_text)
                if val is None:
                    rej.append({"cause": "schema-unsupported",
                                "detail": f"adapter.result.{name} = {raw!r} did not resolve against "
                                          f"this probe's printed output: {why}"})
                    continue
                if name == "findings":
                    findings = val
                elif name == "judged":
                    judged = val
                else:
                    unjudgeable = val
            if judged == 0 and (unjudgeable or 0) > 0:
                rej.append({"cause": "extraction-failed",
                            "detail": f"adapter.result judged 0 with {unjudgeable} unjudgeable"})
            if judged is not None:
                coverage = [{"Population": spec.get("unit", "rows"), "Judged": judged,
                             "Unjudgeable": ({"unjudgeable": unjudgeable} if unjudgeable else {}),
                             "Note": "from adapter.result over the child's printed output"}]

        accepted = not rej
        if accepted:
            result_kind = ((adapter.get("result") or {}).get("kind")
                           if out_kind != "conflict-file"
                           and adapter.get("kind") != "scopeprobe" else "detector")
            if result_kind in ("catalogue", "discovery"):
                # A completed measurement whose rows are EVIDENCE, not defects. It satisfies
                # completion and is never counted as a finding or as a null check.
                detector = "catalogue"
            elif findings is None:
                detector = None
            elif findings > 0:
                detector = "finding"
            else:
                detector = "no-finding"
            if adapter.get("kind") == "scopeprobe" and doc is not None                     and "complete" in doc and not doc.get("complete")                     and not (omission is not None and omission["legal"]):
                detector = "inconclusive"
            if identity:
                ref = self.reference_arm(arm, part)
                self.arm_fingerprints.setdefault(
                    (ref["armId"], str(identity.get("population")), str(identity.get("state"))),
                    identity["fingerprint"])
        return {"accepted": accepted, "rejections": rej, "detector": detector, "findings": findings,
                "coverage": coverage, "verdict": verdict, "identity": identity,
                "identityLimits": identity_limits}

    def _identity_checks(self, identity: dict, arm: dict, doc: dict, adapter: dict,
                         part: str | None = None, limits: list | None = None,
                         declared_reuse: dict | None = None) -> list[dict]:
        """D2: an accepted emit must describe the configuration the plan ASKED FOR.

        The reference is the frozen request of the reference arm (§`reference_arm`), never "whichever
        receipt of this arm arrived first" — agreement between two outputs cannot establish agreement
        with the request, and an incomplete identity is not accepted because it is the first one.

        `declared_reuse` is the origin block `origin_refusals` returned for an import it has ALREADY
        accepted as a well-formed `origin: reused` (§4.8): a capture made for another setup or another
        evaluation. Only rule (f), the candidate FILENAME, is relaxed for it, and only into a recorded
        limit — see there. Every other caller passes nothing and keeps the strict rule, so the run and
        resume lanes are unchanged by construction rather than by argument."""
        rej = []
        if declared_reuse and limits is None:
            # The recorded limit is the whole price of the exception: an acceptance nobody can read about is
            # not a limited relaxation, it is a silent one (review 2 F6). A caller that wants the exception
            # must supply somewhere to put the record.
            raise TypeError("_identity_checks: declared_reuse requires a `limits` list to record into")
        ref = self.reference_arm(arm, part)
        req = ref.get("requested") or {}
        req_plugins = list(req.get("plugins") or [])
        emit_plugins = list(identity.get("plugins") or [])

        # (a) an incomplete identity is never accepted, first receipt or not.
        if not emit_plugins:
            rej.append({"cause": "identity-missing",
                        "detail": "the emit's Configuration lists no plugins: what it consumed cannot "
                                  "be stated, so it cannot be compared to the request"})
            return rej
        for i, p in enumerate(emit_plugins):
            if not (p.get("name") or "").strip():
                rej.append({"cause": "identity-missing",
                            "detail": f"the emit's Configuration plugin at position {i} has no name"})
            elif not is_sha256(p.get("sha256")):
                rej.append({"cause": "identity-missing",
                            "detail": f"the emit's Configuration gives no 64-hex sha256 for "
                                      f"{p.get('name')} (position {i}): its content is not stated"})
        if rej:
            return rej

        if not req_plugins:
            rej.append({"cause": "identity-missing",
                        "detail": f"the plan states no requested plugin list for arm {ref['armId']}, so "
                                  f"this emit cannot be bound to a request"})
            return rej

        # (b) order and names, case-insensitively, position by position.
        want_names = [str(p.get("name") or "") for p in req_plugins]
        got_names = [str(p.get("name") or "") for p in emit_plugins]
        if [n.lower() for n in want_names] != [n.lower() for n in got_names]:
            want_set = {n.lower() for n in want_names}
            got_set = {n.lower() for n in got_names}
            extra = sorted(got_set - want_set)
            absent = sorted(want_set - got_set)
            first = next((i for i in range(max(len(want_names), len(got_names)))
                          if (want_names[i:i + 1] or [""])[0].lower()
                          != (got_names[i:i + 1] or [""])[0].lower()), 0)
            how = ("the same names in a different order" if not extra and not absent else
                   "the set differs (" + "; ".join(
                       ([f"missing: {', '.join(absent)}"] if absent else [])
                       + ([f"extra: {', '.join(extra)}"] if extra else [])) + ")")
            rej.append({"cause": "identity-mismatch",
                        "detail": f"plugin order differs from the request at position {first + 1}: "
                                  f"expected {(want_names[first:first + 1] or ['(nothing)'])[0]}, "
                                  f"found {(got_names[first:first + 1] or ['(nothing)'])[0]}; {how}"})
        else:
            # (c) hashes, wherever the request states one — a hash the scope measured and a hash the
            # plan resolved through the probes' own layer precedence (`hashSource: instance:<layer>`)
            # are checked alike, because both name the bytes this arm asked for. An unstated hash is a
            # recorded LIMIT, never a silent acceptance: the emit's own hash becomes what reuse
            # re-checks on disk. Hashes equal at a different path is accepted and recorded, not
            # rejected: the binding is to content, and a probe that read an identical copy elsewhere
            # measured the same bytes — the reader is told where.
            for want, got in zip(req_plugins, emit_plugins):
                if want.get("hashSource") == "unstated" or not is_sha256(want.get("sha256")):
                    (limits if limits is not None else []).append(
                        {"plugin": want.get("name"), "recordedSha256": got.get("sha256"),
                         "detail": f"the plan could not state a hash for {want.get('name')}; the emit's "
                                   f"hash {str(got.get('sha256'))[:12]} is recorded and is what reuse "
                                   f"re-checks on disk"})
                    continue
                if str(want["sha256"]).lower() != str(got.get("sha256") or "").lower():
                    rej.append({"cause": "identity-mismatch",
                                "detail": f"plugin {want.get('name')} was requested at "
                                          f"{str(want['sha256'])[:12]} ({want.get('path') or '(no path)'})"
                                          f" and consumed at {str(got.get('sha256'))[:12]} "
                                          f"({got.get('path') or '(no path)'})"})
                elif want.get("path") and got.get("path") and not same_path(want["path"], got["path"]):
                    (limits if limits is not None else []).append(
                        {"plugin": want.get("name"), "recordedSha256": got.get("sha256"),
                         "plannedPath": want.get("path"), "emitPath": got.get("path"),
                         "detail": f"the plan resolved {want.get('name')} at {want['path']} and the emit "
                                   f"read an identical copy at {got['path']}: the bytes are the same "
                                   f"{str(got.get('sha256'))[:12]}, the file is not"})

        # (d) overlays as a multiset of (kind, sha256).
        def overlay_key(rows):
            return sorted((str(o.get("kind") or ""), str(o.get("sha256") or "").lower())
                          for o in rows or [])
        if overlay_key(identity.get("overlays")) != overlay_key(req.get("overlays")):
            rej.append({"cause": "identity-mismatch",
                        "detail": f"the emit's overlays are {overlay_key(identity.get('overlays'))}, arm "
                                  f"{ref['armId']} requested {overlay_key(req.get('overlays'))}"})

        # (e) the fingerprint must be the digest of the configuration the emit itself states.
        recomputed = configuration_fingerprint(
            emit_plugins, identity.get("providers"), identity.get("overlays"),
            identity.get("population"), identity.get("state"), schema=identity.get("schemaVersion"))
        if str(identity.get("fingerprint") or "").lower() != recomputed:
            rej.append({"cause": "identity-mismatch",
                        "detail": f"the fingerprint is not the digest of the stated configuration: the "
                                  f"emit says {str(identity.get('fingerprint'))[:12]}, its own plugins, "
                                  f"providers, overlays, population and state digest to "
                                  f"{recomputed[:12]}"})

        # (f) the source must name the reference arm's candidate — except for a declared reuse, whose capture
        # names the candidate of the arm it WAS measured against. Where the two setups' candidate files happen
        # to share a name the rule is satisfied and nothing below happens; where the names DIFFER the rule has
        # no honest answer, because the only way past it would be to edit the capture's own statement of what
        # it measured. The question it stands in for — "was this measured on the configuration this arm asked
        # for?" — is answered above and by the importer's comparison against the frozen request: (b) names and
        # order, (c) content hashes, (d) overlays, (e) the fingerprint as the digest of the stated
        # configuration, and the importer's own equality against the fingerprint recomputed from this arm's
        # request, which binds the plugins and their order, their bytes, the providers and their order, and
        # the population. It does NOT bind state: that comparison takes state from the emit, so the state line
        # is identical on both sides and cancels — state is bound by (g) below, against the plan's selected
        # states. Nothing is rewritten to make (f) pass: the capture keeps its own Source, and a DIFFERENCE
        # becomes a recorded LIMIT so a reader is told the evidence names another file. The digest binds the
        # configuration text and nothing outside it (asset bytes among them); that is stated here, not widened.
        source = (identity.get("source") or "")
        want_source = f"candidate:{ref['candidateFile']}"
        differs = bool(ref["candidateFile"]) and source.lower() != want_source.lower()
        if declared_reuse:
            # The DISCLOSURE, decided on exact inequality and on an absent source - deliberately NOT on the
            # refusal predicate below, whose substring tolerance would let `candidate-01-alternate.json` pass
            # silently for an arm whose file is `candidate-01.json`, and whose `if source` would let an emit
            # that simply omits Source say nothing at all (review 2 F2, 2026-09-17). The disclosure is what
            # pays for the exception, so it is not skippable by shaping or omitting one string.
            if differs:
                src = declared_reuse.get("reusedFrom") or {}
                (limits if limits is not None else []).append(
                    {"capturedAgainst": source or None, "armCandidate": want_source,
                     "declaredSource": {"planId": src.get("planId"), "armId": src.get("armId")},
                     "detail": (f"declared reuse: the capture states it was measured against '{source}', "
                                f"not this arm's '{want_source}'. It is credited because its stated "
                                f"configuration equals this arm's frozen request, not because the files "
                                f"are the same file; the source name is kept as captured, and which run "
                                f"it names is the importer's caller's declaration, not verified here"
                                if source else
                                f"declared reuse: the capture states NO candidate source, so which file it "
                                f"was measured against is not stated at all - this arm's is '{want_source}'. "
                                f"It is credited because its stated configuration equals this arm's frozen "
                                f"request; nothing here says the two are the same file")})
        elif source and ref["candidateFile"] and source.lower() != want_source.lower() \
                and Path(ref["candidateFile"]).stem.lower() not in source.lower():
            rej.append({"cause": "identity-mismatch",
                        "detail": f"the emit's Configuration.Source is '{source}', not this arm's "
                                  f"'{want_source}'"})

        # (g) the within-arm rules, keyed on the REFERENCE arm so a reverse part is compared with the
        # baseline's other emits rather than with the candidate order it was never run on.
        key = (ref["armId"], str(identity.get("population")), str(identity.get("state")))
        prior = self.arm_fingerprints.get(key)
        if prior and identity["fingerprint"] and prior != identity["fingerprint"]:
            rej.append({"cause": "identity-mismatch",
                        "detail": f"fingerprint {identity['fingerprint'][:12]} differs from this arm's "
                                  f"other accepted receipts declaring the same population and state "
                                  f"({prior[:12]})"})
        seen = self.arm_plugin_hashes.setdefault(ref["armId"], {})
        for p in identity.get("plugins", []):
            name = str(p.get("name") or "").lower()
            h = p.get("sha256")
            if not name or not h:
                continue
            if name in seen and seen[name] != h:
                rej.append({"cause": "mixed-snapshot",
                            "detail": f"plugin {name} was consumed as {seen[name][:12]} by another accepted "
                                      f"receipt of this arm and as {h[:12]} here: the inputs changed during "
                                      f"the run and cannot be mixed into one snapshot"})
            seen.setdefault(name, h)
        declared_pop = (adapter or {}).get("population")
        declared_state = (adapter or {}).get("state")
        want_pop = declared_pop if declared_pop is not None else (arm.get("expected") or {}).get("population")
        got_pop = identity.get("population") or doc.get("Population") or doc.get("population")
        got_state = identity.get("state") or doc.get("State") or doc.get("state")
        want_states = ([declared_state] if declared_state is not None
                       else (arm.get("expected") or {}).get("states") or ["baseline"])
        if want_pop and got_pop and str(got_pop) != str(want_pop):
            rej.append({"cause": "population-mismatch",
                        "detail": f"the emit judged population '{got_pop}', the plan's arm is '{want_pop}'"})
        if got_state and str(got_state) not in [str(s) for s in want_states] and str(got_state) != "-":
            rej.append({"cause": "population-mismatch",
                        "detail": f"the emit judged state '{got_state}', the plan selected "
                                  f"{', '.join(str(s) for s in want_states)}"})
        return rej


def write_receipt(run: Path, *, plan: dict, check: dict, arm: dict, part: str | None,
                  adapter: dict, argv: list[str] | None, run_id: str | None, parent_run_id: str | None,
                  started: str | None, ended: str | None, exit_code: int | None,
                  outputs: list[Path], log: Path | None, consumed: dict,
                  validation: dict, result: dict, extra: dict | None = None) -> dict:
    key = Ledger.key(check["checkId"], arm["armId"], part)
    out_rows = []
    for p in outputs:
        if p.exists():
            st = p.stat()
            out_rows.append({"path": rel_to(run, p), "sha256": sha256_file(p),
                             "bytes": st.st_size, "mtimeUtc": iso_of(st.st_mtime)})
        else:
            out_rows.append({"path": rel_to(run, p), "sha256": None, "bytes": None, "mtimeUtc": None})
    receipt = {
        "schemaVersion": SCHEMA_VERSION,
        "receiptId": sha256_text(f"{plan['planId']}|{key}|{run_id}|{started}|{utcnow()}")[:16],
        "planId": plan["planId"], "checkId": check["checkId"], "armId": arm["armId"], "part": part,
        "adapter": adapter, "argvShape": argv_shape(argv, run) if argv else None,
        "runId": run_id, "parentRunId": parent_run_id,
        "startedUtc": started, "endedUtc": ended, "exitCode": exit_code,
        "outputs": out_rows, "log": rel_to(run, log) if log and log.exists() else None,
        "consumed": consumed,
        "validation": {"accepted": bool(validation.get("accepted")),
                       "rejections": validation.get("rejections", []),
                       # D2(c): a request plugin whose hash the plan could not state is accepted with
                       # the emit's own hash recorded HERE, so the limit travels with the receipt.
                       "identityLimits": validation.get("identityLimits") or []},
        "result": result,
    }
    receipt.update(extra or {})
    write_json(run / "receipts" / f"{key}.json", receipt)
    return receipt


# --------------------------------------------------------------------------- the executor --

class Executor:
    def __init__(self, run: Path, plan: dict, ledger: Ledger, *, resume: bool):
        self.run = Path(run)
        self.plan = plan
        self.ledger = ledger
        self.resume = resume
        self.opts = plan["options"]
        self.harness = Path(self.opts["harness"])
        self.validator = Validator(self.run, plan)
        self.logs = self.run / "logs"
        self.parent_run_id = None
        # The budget is a DEADLINE for this invocation, measured from here with a high-resolution
        # monotonic clock (perf_counter: time.monotonic's ~16 ms tick on Windows let a budget shorter
        # than one tick launch a child). `run` and `resume` each start their own deadline from the
        # plan's frozen minutes. The Runner enforces it on the process tree it launched.
        self.t0 = time.perf_counter()
        self.budget_s = (plan["budgets"].get("minutes") or 0) * 60 or None
        self.deadline = (self.t0 + self.budget_s) if self.budget_s else None
        self.runner = Runner("evalrun-check", out_dir=self.logs, retries=0, deadline=self.deadline)
        self.deadline_stops = 0
        self.arms = {a["armId"]: a for a in plan["arms"]}
        self.options_sha = sha256_text(canonical(self.opts))
        self.children = 0

    # -- budgeting ------------------------------------------------------------------------

    def budget_left(self) -> bool:
        """Checked BEFORE a child launches, so an expired budget starts nothing. A child still running
        when the deadline passes is stopped by the Runner - its whole launched process tree - and is
        accounted `stopped(deadline)`; see `stop_sentence`."""
        return self.deadline is None or time.perf_counter() < self.deadline

    def budget_sentence(self) -> str:
        return (f"the plan's budget of {self.plan['budgets']['minutes']} minute(s) was reached "
                f"{(time.perf_counter() - self.t0) / 60:.1f} minutes into this run, before this check "
                f"launched: it was planned and not evaluated")

    def stop_sentence(self, res: dict, what: str) -> str:
        return (f"the plan's budget of {self.plan['budgets']['minutes']} minute(s) was reached while {what} "
                f"was running: its process tree was stopped at the deadline after {res.get('seconds', 0):.1f}s "
                f"({'confirmed gone' if res.get('treeStopped') else 'NOT confirmed gone'}); nothing it "
                f"wrote is accepted, and the check was planned and not evaluated")

    def account_stop(self, check: dict, arm: dict, part: str | None, res: dict, what: str,
                     log: Path | None = None) -> None:
        """A deadline outcome for one (check, arm, part): `stopped(deadline)` when a child was stopped
        mid-run, `skipped-budget` when the Runner refused to launch it. Never accepted, never read."""
        if res.get("stopped") == "before-launch":
            self.account(check, arm, "skipped-budget", "budget-exhausted", self.budget_sentence(), part=part)
            return
        if res.get("stopped") == "uncontained":
            # Not the budget: the Runner could not contain the child's process tree, so the deadline could
            # not be enforced on it, and it terminated the child before it ran.
            self.account(check, arm, "blocked(uncontained)", None,
                         f"{what} never ran: its process could not be contained, so the deadline could not be "
                         f"enforced on its tree, and it was terminated before it started "
                         f"({res.get('refused') or 'no reason given'}); nothing was measured",
                         part=part, run_id=res.get("runId"), log=log)
            return
        # exit_code stays None: a stopped child returned nothing. The Runner's code is the stop it imposed, or
        # the launched process's own code when that process had exited but its tree had not; the latter is
        # carried only in the detail, never as the receipt's exit code.
        detail = self.stop_sentence(res, what)
        if res.get("launchedExitCode") is not None:
            detail += f" (the launched process itself had exited with code {res['launchedExitCode']})"
        self.account(check, arm, "stopped(deadline)", "budget-exhausted", detail,
                     part=part, run_id=res.get("runId"), log=log, exit_code=None)

    # -- the common consumed block ---------------------------------------------------------

    def consumed_for(self, arm: dict, adapter: dict, *, base_emit: Path | None = None,
                     identity: dict | None = None, inputs: list | None = None) -> dict:
        """What this child actually read, as strongly as the child stated it.

        D3: a child that states no identity — a json / text / probe-target adapter, or an accounting
        row that ran nothing — gets THIS ARM's frozen request, with `configurationSource: "plan"` and a
        null fingerprint plus a note. The scope's baseline fingerprint is never copied onto another
        arm: that would present the baseline's identity as proof of what a different order consumed.
        The baseline arm is the one place where the scope's configuration IS this arm's, so it is
        recorded verbatim with `configurationSource: "scope"`."""
        exe = adapter.get("exe")
        tool = self.plan["tools"].get(exe) if exe else None
        if identity is not None:
            cfg, source = dict(identity), "emit"
        elif arm.get("kind") == "baseline":
            cfg, source = {
                "fingerprint": self.plan["configuration"].get("fingerprint"),
                "source": self.plan["configuration"].get("source"),
                "plugins": self.plan["configuration"].get("plugins", []),
                "providers": self.plan["configuration"].get("providers", []),
                "overlays": self.plan["configuration"].get("overlays", []),
                "population": self.plan["configuration"].get("population"),
                "state": None, "limits": [],
            }, "scope"
        else:
            req = arm.get("requested") or {}
            cfg, source = {
                "fingerprint": None,
                "fingerprintNote": "the child stated no identity; reuse re-checks the plan's stated "
                                   "plugin hashes on disk",
                "source": f"candidate:{arm.get('candidateFile')}",
                # D3: the request's rows WITH the `hashSource` that says where each hash came from —
                # the measured scope, or the layer the plan resolved the file in. A reader of this
                # receipt alone can tell a scope-measured input from a plan-resolved one.
                "plugins": [{"name": p.get("name"), "path": p.get("path"), "sha256": p.get("sha256"),
                             "hashSource": p.get("hashSource")}
                            for p in (req.get("plugins") or [])],
                "providers": [dict(v) for v in (req.get("providers") or [])],
                "overlays": [dict(o) for o in (req.get("overlays") or [])],
                "population": req.get("population") or self.plan["configuration"].get("population"),
                "state": None, "limits": [],
            }, "plan"
        if source != "emit":
            # Only an `unstated` row is a reuse gap. A row the plan resolved states a path and a hash,
            # so reuse re-checks its bytes at the path the plan named, like any scope-stated row.
            unstated = [p.get("name") for p in ((arm.get("requested") or {}).get("plugins") or [])
                        if p.get("hashSource") == "unstated" or not is_sha256(p.get("sha256"))]
            if unstated:
                cfg["identityLimits"] = [
                    {"plugin": n, "recordedSha256": None,
                     "detail": f"the plan states no hash for {n}, so reuse cannot re-check its bytes: "
                               f"this plugin is a named reuse gap, not a verified input"}
                    for n in unstated]
            if not cfg.get("plugins"):
                cfg["reuseGap"] = ("scope.json states no plugin paths and hashes for this configuration, "
                                   "so what this child consumed cannot be re-checked on disk")
        block = {
            "candidateSha256": arm.get("candidateSha256"),
            "candidatePath": arm.get("candidatePath"),
            "configuration": cfg,
            "configurationSource": source,
            "baseEmitSha256": sha256_file(base_emit) if base_emit and base_emit.exists() else None,
            "baseEmitPath": rel_to(self.run, base_emit) if base_emit else None,
            "registrySha256": self.plan["registry"]["sha256"],
            "tool": tool,
            "optionsSha256": self.options_sha,
            "scopeSha256": self.plan["scope"]["sha256"],
        }
        # D4: the comparison a receipt IS. A diff rests on two emits; reuse must follow both.
        if inputs is not None:
            block["inputs"] = inputs
        return block

    # -- the accounting adapters (never run a child) ---------------------------------------

    def account(self, check: dict, arm: dict, status: str, cause: str | None, detail: str, *,
                part: str | None = None, run_id: str | None = None, log: Path | None = None,
                exit_code: int | None = None, extra: dict | None = None) -> None:
        adapter = check["adapter"]
        validation = {"accepted": False, "rejections": ([{"cause": cause, "detail": detail}] if cause else [])}
        result = {"execution": status, "detector": None, "findings": None, "coverage": [],
                  "strength": (check.get("evidence") or {}).get("strength"), "verdict": None,
                  "note": detail}
        write_receipt(self.run, plan=self.plan, check=check, arm=arm, part=part, adapter=adapter,
                      argv=None, run_id=run_id, parent_run_id=self.parent_run_id,
                      started=None, ended=utcnow(), exit_code=exit_code, outputs=[], log=log,
                      consumed=self.consumed_for(arm, adapter), validation=validation, result=result,
                      extra=extra)
        stopped = status == "stopped(deadline)"
        uncontained = status == "blocked(uncontained)"
        self.ledger.transition(check["checkId"], arm["armId"], part, status, cause=cause,
                               rejections=validation["rejections"],
                               detail=detail if (stopped or uncontained) else None,
                               next_action=("`resume --run RUN` re-runs it under a fresh deadline" if stopped
                                            else "`resume --run RUN` re-runs it; it never ran" if uncontained
                                            else None))

    # -- one child --------------------------------------------------------------------------

    def run_child(self, check: dict, arm: dict, part: str | None, argv: list[str],
                  outputs: list[Path], *, base_emit: Path | None = None,
                  job_suffix: str = "", states: list | None = None) -> dict:
        adapter = check["adapter"]
        key = Ledger.key(check["checkId"], arm["armId"], part)
        job_id = (key + job_suffix)[:100]
        for p in outputs:
            p.parent.mkdir(parents=True, exist_ok=True)
        pre = {str(p): sha256_file(p) for p in outputs}
        self.ledger.transition(check["checkId"], arm["armId"], part, "running")
        started_iso, started_mono = utcnow(), time.time()
        res = self.runner.run([{"id": job_id, "argv": argv, "env": self.child_env()}])[0]
        log = self.logs / f"{job_id}.txt"
        if res.get("stopped"):
            # Defect iv: a child stopped at (or refused by) the deadline is never validated - whatever
            # it wrote before the stop is partial, and its log carries no completion trailer.
            if res["stopped"] == "deadline":
                self.children += 1
                self.deadline_stops += 1
            self.account_stop(check, arm, part, res, f"check {check['checkId']} [{arm['armId']}]",
                              log=log if log.exists() else None)
            return {}
        self.children += 1
        ended_iso = utcnow()
        text = log.read_text(encoding="utf-8", errors="replace") if log.exists() else ""
        if (adapter.get("output") or "conflict-file") == "text" and log.exists():
            outputs = [log]
        v = self.validator.validate(check=check, arm=arm, part=part, adapter=adapter,
                                    exit_code=res["exitCode"], log_text=text, outputs=outputs,
                                    pre_hashes=pre, started=started_mono)
        return self.finish(check, arm, part, adapter, argv, res, started_iso, ended_iso, outputs,
                           log, v, base_emit=base_emit,
                           extra={"states": states} if states else None)

    def finish(self, check, arm, part, adapter, argv, res, started_iso, ended_iso, outputs, log, v,
               *, base_emit=None, extra=None, verdict=None, execution=None, inputs=None) -> dict:
        if v["accepted"]:
            status = execution or "completed"
        elif v["rejections"]:
            status = f"rejected({v['rejections'][0]['cause']})"
        else:
            status = execution or "failed"
        result = {"execution": status, "detector": v["detector"], "findings": v["findings"],
                  "coverage": v["coverage"],
                  "strength": (check.get("evidence") or {}).get("strength"),
                  "verdict": verdict if verdict is not None else v.get("verdict")}
        if v["accepted"] and v["detector"] is None:
            result["detectorUndetermined"] = (
                "this adapter's output contract does not say where a finding count lives "
                "(no adapter.findings path), so the detector result is undetermined rather than null-as-clean")
        receipt = write_receipt(self.run, plan=self.plan, check=check, arm=arm, part=part,
                                adapter=adapter, argv=argv,
                                run_id=(res or {}).get("runId"), parent_run_id=self.parent_run_id,
                                started=started_iso, ended=ended_iso,
                                exit_code=(res or {}).get("exitCode"), outputs=outputs, log=log,
                                consumed=self.consumed_for(arm, adapter, base_emit=base_emit,
                                                           identity=v.get("identity"), inputs=inputs),
                                validation=v, result=result, extra=extra)
        self.ledger.transition(check["checkId"], arm["armId"], part, status,
                               cause=(v["rejections"][0]["cause"] if v["rejections"] else None),
                               receipt_id=receipt["receiptId"], detector=v["detector"],
                               rejections=v["rejections"],
                               detail=(v["rejections"][0]["detail"] if v["rejections"] else None),
                               next_action=("re-run this check once the cause above is fixed"
                                            if v["rejections"] else None),
                               extra={"derivedFrom": (extra or {}).get("derivedFrom")})
        return receipt

    # -- argv assembly ----------------------------------------------------------------------

    def child_env(self) -> dict:
        """HarnessTarget reads the instance from the environment, so a probe with no --instance flag
        still resolves the same configuration the flagged ones do."""
        env = {}
        for var, key in (("HOUSECARL_MO2_INSTANCE", "instance"), ("HOUSECARL_MO2_PROFILE", "profile"),
                         ("HOUSECARL_GAME_PATH", "game")):
            if self.opts.get(key):
                env[var] = str(self.opts[key])
        return env

    def common_flags(self) -> list[str]:
        out = []
        for flag in ("instance", "profile", "game"):
            if self.opts.get(flag):
                out += [f"--{flag}", self.opts[flag]]
        return out

    def exe_argv(self, adapter: dict) -> list[str] | None:
        """The child's program. `--probe-cmd NAME=<argv prefix>` stands a fixture's fake probe in."""
        exe = adapter.get("exe")
        if not exe:
            return None
        override = (self.opts.get("probeCmd") or {}).get(exe)
        if override:
            return override.split()
        path = probe_exe(self.harness, exe)
        if not path.exists():
            return None
        return [str(path)]

    def expand(self, args: list[str], subs: dict) -> list[str]:
        out = []
        for tok in args:
            for k, val in subs.items():
                tok = tok.replace("{" + k + "}", str(val))
            out.append(tok)
        return out


# ------------------------------------------------------------------------- the run command --

def cmd_run(a, *, resuming: bool = False) -> int:
    run = Path(a.run)
    set_run_store(run)
    plan, why = load_plan(run)
    if plan is None:
        return refuse(why)
    why = request_refusal(plan)
    if why:
        return refuse(why)
    ledger = Ledger(run, plan)
    if getattr(a, "rebuild_ledger", False):
        ledger.rebuild()
    bad = ledger.refusal()
    if bad:
        return refuse(bad)

    bad = premise_refusal(plan)
    if bad:
        return refuse(bad)

    with Run("evalrun-checks", stage_source="counted") as parent:
        ledger.open_invocation("resume" if resuming else "run", parent.run_id, None)
        ex = Executor(run, plan, ledger, resume=resuming)
        ex.parent_run_id = parent.run_id
        healed = ledger.heal_interrupted()
        if healed:
            print(f"   {healed} interrupted entr{'y' if healed == 1 else 'ies'} re-queued")

        # D5: `run` and `resume` offer the SAME correctness guarantee. A run over a ledger that already
        # holds terminal-ok entries is a reuse decision whatever the caller typed, so it revalidates
        # too — the verb chosen never decides whether measured evidence is re-checked against disk.
        if resuming or any(e.get("status") in TERMINAL_OK
                           for e in ledger.doc["entries"].values()):
            revalidate(run, plan, ledger, ex)

        planned = [c for c in plan["checks"]]
        parent.feature("checks", len(planned), "checks")
        parent.feature("arms", len(plan["arms"]), "arms")
        parent.feature("probes", len(plan["ordersweepProbes"]), "probes")
        parent.stage("checks", total=sum(len(c["arms"]) for c in planned), unit="check-arms")

        execute(ex, parent)
        if ex.deadline_stops:
            parent.note(f"deadline: {ex.deadline_stops} running child(ren) stopped at the budget's deadline")
            print(f"   deadline: {ex.deadline_stops} running child(ren) stopped when the plan's budget of "
                  f"{plan['budgets']['minutes']} minute(s) ran out; their checks are stopped(deadline) and "
                  f"`resume` re-runs them under a fresh deadline")

    print_status(run, plan, ledger)
    return EXIT_OK


def schedule(plan: dict) -> list[dict]:
    """The order checks run in: a topological pass over the prerequisite graph in which, among the
    checks whose prerequisites are ALREADY SCHEDULED, required ones go first and registry order
    breaks ties.

    Required-first means required-first among the checks that are READY, not required-first by
    prerequisite depth. Ordering by depth put every optional check with no prerequisites ahead of a
    required check that had one, which is how an optional download-pool probe spent six minutes of
    the budget before the required geometry sweep had started. An optional check with no
    prerequisites now waits while any required check is ready.

    A prerequisite naming an unknown id, or a cycle the registry's own validation did not catch,
    cannot stall the pass: what remains is appended in registry order rather than dropped."""
    checks = list(plan["checks"])
    by_id = {c["checkId"]: c for c in checks}
    order = {c["checkId"]: i for i, c in enumerate(checks)}
    pending = {c["checkId"] for c in checks}
    done: set[str] = set()
    out: list[dict] = []

    while pending:
        ready = [cid for cid in pending
                 if all(pre in done or pre not in by_id
                        for pre in (by_id[cid].get("prerequisites") or []))]
        if not ready:
            # A cycle, or a prerequisite outside the registry: append the rest rather than stall.
            out += [by_id[cid] for cid in sorted(pending, key=lambda x: order[x])]
            break
        cid = min(ready, key=lambda x: (0 if by_id[x].get("required") else 1, order[x]))
        out.append(by_id[cid])
        pending.discard(cid)
        done.add(cid)
    return out


def execute(ex: Executor, parent) -> None:
    plan, ledger, run = ex.plan, ex.ledger, ex.run
    checks = {c["checkId"]: c for c in plan["checks"]}

    def already_done(cid: str, aid: str, part: str | None) -> bool:
        e = ledger.entry(cid, aid, part)
        return bool(e and e.get("status") in TERMINAL_OK)

    def mark_reused(cid: str, aid: str, part: str | None) -> None:
        e = ledger.entry(cid, aid, part)
        if e and e.get("status") == "completed":
            ledger.transition(cid, aid, part, "reused", receipt_id=e.get("receiptId"),
                              detector=e.get("detector"),
                              extra={"reusedFromRunId": e.get("runId")})

    # The plan's checks are already in prerequisite order (evalregistry topologically sorted them).
    # The ordersweep seam is ONE child for every ordersweep-probe check, run at the position of the
    # FIRST such check — so a prerequisite ahead of it really has run before it, and a check behind
    # it can read its receipts.
    sweep_checks = [c for c in plan["checks"]
                    if c["adapter"].get("kind") == "ordersweep-probe" and c["status"] == "planned"]
    sweep_done = False

    for check in schedule(plan):
        kind = check["adapter"].get("kind")
        if kind == "ordersweep-probe" and check in sweep_checks:
            if not sweep_done:
                allowed = sweep_arms_allowed(ex, sweep_checks)
                pending = sweep_needed(ex, sweep_checks, {})
                if pending and not ex.budget_left():
                    # The budget first: a prerequisite that is unsettled because the deadline passed is the
                    # budget's consequence, and naming it blocked(prerequisite) would hide that cause.
                    for c in sweep_checks:
                        for r in c["arms"]:
                            ex.account(c, ex.arms[r["armId"]], "skipped-budget", "budget-exhausted",
                                       ex.budget_sentence())
                elif not pending or not sweep_needed(ex, sweep_checks, allowed):
                    # Nothing the sweep could settle: it is not re-run. `run` twice on an unchanged RUN
                    # performs no child work at all, and writes no MO2 profile. An arm its prerequisite keeps
                    # out, or the plan never measures, is accounted here without a launch.
                    account_sweep_kept_out(ex, sweep_checks, allowed)
                    memo = HashMemo()
                    for c in sweep_checks:
                        for r in c["arms"]:
                            arm = ex.arms[r["armId"]]
                            if allowed.get(r["armId"], "ok") != "ok":
                                continue
                            why = candidate_stale(arm, memo) if r["status"] == "planned" else None
                            if why:
                                gone = not Path(arm["candidatePath"]).exists()
                                st = "blocked(candidate-missing)" if gone else "blocked(candidate-changed)"
                                if (ex.ledger.entry(c["checkId"], r["armId"], None) or {}).get("status") != st:
                                    ex.account(c, arm, st, "output-missing" if gone else "stale-inputs",
                                               f"the plan's candidate file {arm['candidatePath']} is gone" if gone
                                               else why)
                                continue
                            for part in (("arm",) if arm["kind"] == "baseline" else SWEEP_PARTS):
                                if not account_unplanned_sweep_part(ex, c, arm, r["status"], part):
                                    mark_reused(c["checkId"], r["armId"], part)
                    kept = sorted({a for a, v in allowed.items() if v != "ok"})
                    print("   ordersweep: every arm reused, no child run" if not kept else
                          f"   ordersweep: nothing to measure, no child run ({len(kept)} arm(s) kept out by a "
                          f"prerequisite: {', '.join(kept)})")
                elif ex.budget_left():
                    run_ordersweep(ex, sweep_checks, parent, allowed=allowed)
                else:
                    for c in sweep_checks:
                        for r in c["arms"]:
                            ex.account(c, ex.arms[r["armId"]], "skipped-budget", "budget-exhausted",
                                       ex.budget_sentence())
                sweep_done = True
            continue
        for arm_row in check["arms"]:
            arm = ex.arms[arm_row["armId"]]
            st = arm_row["status"]
            if already_done(check["checkId"], arm["armId"], None):
                pre = reuse_blocked_by(ex, check, arm["armId"]) if st == "planned" and kind != "media" else None
                if st in IMPORT_ROW_STATUSES:
                    # Only an import credits an mcp-only (or live-import) row, and it was admitted by `prereq_satisfied`: it stays
                    # credited only while that same gate still holds on this arm.
                    pre = prereq_satisfied(ex, check, arm["armId"])
                if kind == "derived":
                    pass  # reused or blocked after the loop, where derived checks are decided
                elif pre:
                    ex.account(check, arm, f"blocked(prerequisite:{pre})", "blocked-prerequisite",
                               prereq_block_detail(ex, pre, arm["armId"])
                               + ("" if st not in IMPORT_ROW_STATUSES else
                                  ". The imported evidence stays under imports/ but is no longer credited; import it "
                                  "again once the prerequisite is settled on this arm"),
                               extra=superseded_import(ex, check, arm))
                else:
                    mark_reused(check["checkId"], arm["armId"], None)
                parent.advance()
                continue
            if st == "not-applicable":
                ex.account(check, arm, "not-applicable", None, check.get("applicabilityEvidence") or "")
            elif st == "excluded":
                ex.account(check, arm, "skipped-user", None,
                           plan["explicitDecisions"]["exclude"].get(check["checkId"], "excluded by the caller"))
            elif st == "skipped-user":
                ex.account(check, arm, "skipped-user", None,
                           "not in --select; --select narrows what runs, never what is planned")
            elif st == "unsupported-for-arm":
                ex.account(check, arm, "unsupported", None,
                           f"this adapter does not accept the {arm['kind']} arm as an input the plan can hand it")
            elif st == "skipped-budget":
                ex.account(check, arm, "skipped-budget", "budget-exhausted",
                           f"arm {arm['armId']} was held back by --max-candidates "
                           f"{plan['budgets']['maxCandidates']}: it was planned and not evaluated")
            elif st == "blocked-payload-missing":
                ex.account(check, arm, "blocked(payload-missing)", "blocked-prerequisite",
                           "the supplied arm declares files that are not in the configuration: "
                           + ", ".join(arm["supplied"]["payloadMissing"]))
            elif st == NOT_OPTED_IN:
                ex.account(check, arm, "skipped-user", None,
                           "opt-in check, not requested (`plan --opt-in " + check["checkId"] + "`): it was not run "
                           "and this evaluation says nothing about it")
            elif st in IMPORT_ROW_STATUSES and uncredited_import_still_blocked(ex, check, arm["armId"]):
                # An import this gate un-credited earlier stays blocked, by name, while its prerequisite still fails;
                # once the prerequisite holds it is an ordinary mcp-only row again, waiting for a re-import.
                pre = prereq_satisfied(ex, check, arm["armId"])
                ex.account(check, arm, f"blocked(prerequisite:{pre})", "blocked-prerequisite",
                           prereq_block_detail(ex, pre, arm["armId"])
                           + ". The imported evidence stays under imports/ but is no longer credited; import it "
                             "again once the prerequisite is settled on this arm",
                           extra=superseded_import(ex, check, arm))
            elif st in IMPORT_ROW_STATUSES and uncredited_import_cause(ex, check, arm["armId"]):
                # An import whose entry was invalidated or refused reuse (its inputs, or a prerequisite's evidence,
                # changed) keeps THAT cause: flattening it to a plain mcp-only row would say no import was ever made.
                was = uncredited_import_cause(ex, check, arm["armId"])
                also = prereq_satisfied(ex, check, arm["armId"])
                ex.account(check, arm, was, "stale-inputs",
                           "an accepted import of this check is no longer credited: " + was
                           + ". Its copied evidence stays under imports/ but backs no receipt; import it again "
                             "(the re-import is copied beside the old folder) once its inputs are what it states"
                           + ("" if not also else
                              f". A re-import is refused until {also} is settled on this arm as well: "
                              + prereq_block_detail(ex, also, arm["armId"])),
                           extra=superseded_import(ex, check, arm))
            elif st == "live-import":
                ex.account(check, arm, "live-import", None,
                           "opted in and waiting for a live receipt: run switchflip.py for this arm, then "
                           "`import-results --file` its export; the controller itself never launches the game")
            elif st in ("unsupported", "mcp-only"):
                ex.account(check, arm, st, None, check.get("unsupportedReason") or
                           "the registry records no CLI adapter for this check")
            elif st == "manual":
                ex.account(check, arm, "manual", None, check.get("unsupportedReason") or
                           "this check is completed by an attributed manual decision, read at finalize")
            elif kind == "media":
                ex.account(check, arm, "blocked(no-identified-media)", None,
                           "no supplied media identified this check's configuration on this arm: media is "
                           "assessed by `bundle --media`, and an item whose fingerprint does not match an "
                           "accepted receipt is unassessed, not an illustration of a verified result")
            elif kind == "derived":
                pass  # after the loop: derived checks consume other receipts
            else:
                # The derived gate's rule (`decided_on_arm`): a prerequisite measured and failed on this arm, or
                # blocked on it by such a failure, blocks it here even when the baseline's was accepted; one not
                # attempted falls back.
                missing = [p for p in check["prerequisites"]
                           if not any((ledger.entry(p, aid, None) or {}).get("status") in TERMINAL_OK
                                      for aid in prereq_answer_arms(ex, p, arm["armId"]))]
                # The budget first: once the deadline has passed, a prerequisite that is missing because the
                # deadline stopped it is the budget's consequence, and naming it blocked(prerequisite) would
                # hide that cause.
                if not ex.budget_left():
                    ex.account(check, arm, "skipped-budget", "budget-exhausted", ex.budget_sentence())
                elif missing:
                    ex.account(check, arm, f"blocked(prerequisite:{missing[0]})", "blocked-prerequisite",
                               "a prerequisite check has no accepted receipt: " + ", ".join(
                                   f"{p} ({(ledger.entry(p, arm['armId'], None) or {}).get('status')} on this arm)"
                                   if len(prereq_answer_arms(ex, p, arm["armId"])) == 1 else p
                                   for p in missing))
                else:
                    run_generic(ex, check, arm)
            parent.advance()

    for check in schedule(plan):
        if check["adapter"].get("kind") != "derived" or check["status"] != "planned":
            continue
        for arm_row in check["arms"]:
            if arm_row["status"] != "planned":
                continue  # its plan status (held back, excluded, not applicable, ...) was accounted in the loop above
            arm = ex.arms[arm_row["armId"]]
            if already_done(check["checkId"], arm["armId"], None):
                pre = reuse_blocked_by(ex, check, arm["armId"])
                if pre:
                    ex.account(check, arm, f"blocked(prerequisite:{pre})", "blocked-prerequisite",
                               prereq_block_detail(ex, pre, arm["armId"]))
                else:
                    mark_reused(check["checkId"], arm["armId"], None)
                continue
            run_derived(ex, check, arm)


#: The parts one swept (check, arm) settles, in the order they rest on each other.
SWEEP_PARTS = ("arm", "reverse", "diff")


def sweep_parts(ex: Executor, arm: dict) -> tuple[str, ...]:
    """The parts the sweep is planned to settle for an arm: the baseline only its `arm`; a candidate its `arm`, its
    `diff` and, unless `--no-reverse`, its `reverse` (a reverse that was never going to run is not a gap to repair)."""
    if arm["kind"] == "baseline":
        return ("arm",)
    return ("arm", "diff") if ex.opts.get("noReverse") else SWEEP_PARTS


def sweep_needed(ex: Executor, sweep_checks: list[dict], allowed: dict) -> bool:
    """True when launching the sweep could settle something: an arm the plan measures and its prerequisites allow has
    a planned part that is not terminal-ok. EVERY part is looked at, not only `arm`: a resume after a partial sweep
    must repair the reverse or diff it left. An arm its prerequisite keeps out is accounted without a launch - the
    sweep would not measure it - so an unchanged run does not relaunch for it; a blocked baseline launches nothing."""
    if allowed.get("baseline", "ok") != "ok":
        return False
    memo = HashMemo()
    for c in sweep_checks:
        for r in c["arms"]:
            if r["status"] != "planned" or allowed.get(r["armId"], "ok") != "ok":
                continue
            if candidate_stale(ex.arms[r["armId"]], memo):
                continue  # the plan no longer describes its order: launching cannot settle it (plan again)
            for part in sweep_parts(ex, ex.arms[r["armId"]]):
                e = ex.ledger.entry(c["checkId"], r["armId"], part)
                if not e or e.get("status") not in TERMINAL_OK:
                    return True
    return False


def account_sweep_kept_out(ex: Executor, sweep_checks: list[dict], allowed: dict) -> None:
    """Every arm its prerequisite keeps out of the sweep reads `blocked(prerequisite:<id>)`, on the arm and on each part
    an earlier invocation settled for it: those parts are not reused under a prerequisite that now blocks them. An
    entry already carrying that status is left as it is, so an unchanged run rewrites nothing."""
    for c in sweep_checks:
        for r in c["arms"]:
            state = allowed.get(r["armId"], "ok")
            if state == "ok":
                continue
            pre = state.split(":", 1)[1]
            status = f"blocked(prerequisite:{pre})"
            detail = prereq_block_detail(ex, pre, r["armId"])
            for part in (None,) + SWEEP_PARTS:
                e = ex.ledger.entry(c["checkId"], r["armId"], part)
                if (part is not None and e is None) or (e is not None and e.get("status") == status):
                    continue
                ex.account(c, ex.arms[r["armId"]], status, "blocked-prerequisite", detail, part=part)


def account_unplanned_sweep_part(ex: Executor, check: dict, arm: dict, st: str | None, part: str) -> bool:
    """Account one part of an arm the plan never meant this sweep to measure, with its plan status as the cause.
    False (nothing accounted) for a `planned` arm. An entry already carrying that status is left as it is."""
    if st == "planned" or st is None:
        return False
    aid = arm["armId"]
    if st == "skipped-budget":
        # held back by --max-candidates at plan time: that, not the deadline or the sweep, is its cause
        status, cause = "skipped-budget", "budget-exhausted"
        detail = (f"arm {aid} was held back by --max-candidates {ex.plan['budgets']['maxCandidates']}: it was planned "
                  f"and not evaluated")
    elif st == "blocked-payload-missing":
        status, cause = "blocked(payload-missing)", "blocked-prerequisite"
        detail = ("the supplied arm declares files that are not in the configuration: "
                  + ", ".join(arm["supplied"]["payloadMissing"]))
    else:
        status = ("not-applicable" if st == "not-applicable" else
                  "skipped-user" if st in ("excluded", "skipped-user", NOT_OPTED_IN) else "unsupported")
        cause, detail = None, f"plan status {st}: this arm was not going to be measured"
    if (ex.ledger.entry(check["checkId"], aid, part) or {}).get("status") != status:
        ex.account(check, arm, status, cause, detail, part=part)
    return True


PLACEHOLDERS = {"order", "emit", "json", "base", "out", "run", "arm", "target", "nexusId",
                "switchMap", "pair"}


def plan_nexus_id(plan: dict):
    for t in plan.get("targets") or []:
        if isinstance(t, dict):
            nid = (t.get("footprint") or {}).get("nexusId") or t.get("nexusId")
            if nid:
                return nid
    return None


def target_pairs(ex: Executor, arm: dict):
    """The plugin pairs a patch check is about, computed STRUCTURALLY from the plugin lists rather
    than parsed out of a human sentence: every pair whose relative order FLIPS between the baseline
    and this arm, plus, for a supplied arm, (each plan target, the supplied plugin). The manifest's
    `Moves` is a count, not a list, so it cannot be read for this."""
    base = next((a for a in ex.plan["arms"] if a["kind"] == "baseline"), None)
    pairs, seen = [], set()

    def add(x, y):
        key = tuple(sorted([x, y], key=str.lower))
        if key not in seen and key[0].lower() != key[1].lower():
            seen.add(key)
            pairs.append(list(key))

    if base and arm["armId"] != base["armId"]:
        b = [str(x) for x in (base.get("plugins") or [])]
        c = [str(x) for x in (arm.get("plugins") or [])]
        bi = {n.lower(): i for i, n in enumerate(b)}
        ci = {n.lower(): i for i, n in enumerate(c)}
        common = [n for n in c if n.lower() in bi]
        for i, x in enumerate(common):
            for y in common[i + 1:]:
                if (bi[x.lower()] < bi[y.lower()]) != (ci[x.lower()] < ci[y.lower()]):
                    add(x, y)

    targets = [str(t.get("name")) for t in (ex.plan.get("targets") or [])
               if isinstance(t, dict) and t.get("name")]
    for supplied in (arm.get("supplied", {}) or {}).get("declaredFiles") or []:
        for t in targets:
            add(t, Path(supplied).name)
    return pairs


def all_plan_pairs(ex: Executor) -> list:
    """Every pair the WHOLE plan is about. A discovery check runs on the baseline arm, but what a
    patch would have to reconcile is what the candidate arms move: deriving the pairs from the
    baseline alone yields none, which is not the same as none existing."""
    out, seen = [], set()
    for arm in ex.plan["arms"]:
        for pair in target_pairs(ex, arm):
            key = tuple(sorted(pair, key=str.lower))
            if key not in seen:
                seen.add(key)
                out.append(list(key))
    return out


#: Kinds that take --instance/--profile/--game on the command line when the registry does not say.
#: A flag-less probe (blockprobe, patchprobe, fomodprobe, poolprobe) finds the instance through
#: HarnessTarget's environment variables instead, which every child receives.
COMMON_FLAG_KINDS = {"scopeprobe", "ordersweep-probe", "probe-emit", "probe-json"}


def wants_common_flags(adapter: dict) -> bool:
    declared = adapter.get("commonFlags")
    if declared is not None:
        return bool(declared)
    return adapter.get("kind") in COMMON_FLAG_KINDS


def states_for(check: dict, plan: dict) -> list[str]:
    """The world states this check is asked to evaluate: every SELECTED state other than baseline.
    Baseline is the load-time state the probe judges without being told."""
    if (check.get("inputs") or {}).get("states") != "selected":
        return []
    return [s for s in (plan["states"]["selected"] or []) if str(s).lower() != "baseline"]


def run_generic(ex: Executor, check: dict, arm: dict) -> None:
    """scopeprobe / probe-emit / probe-json / probe-target / adviceprobe / patchprobe.

    The probe-specific FLAGS come from the registry as data, with placeholders this controller
    fills from NAMED sources. That keeps the tool surface generic: a new probe is a registry row,
    not a verb here. A placeholder whose source is absent blocks the check by name; a flag the
    probe does not accept is never appended."""
    adapter = check["adapter"]
    kind = adapter.get("kind")
    stale = candidate_stale(arm)
    if stale:
        # The frozen candidate hash gates the LAUNCH, not just the receipt. A child read the changed
        # file while the receipt recorded the plan's hash for it, which is a measurement of an order
        # nobody approved, filed under the order they did.
        ex.account(check, arm, "blocked(candidate-changed)", "stale-inputs", stale)
        return
    prog = ex.exe_argv(adapter)
    if prog is None:
        ex.account(check, arm, "blocked(tool-missing)", None,
                   f"no Release binary for '{adapter.get('exe')}' under {ex.harness} - build it, "
                   f"or stand one in with --probe-cmd")
        return

    states = states_for(check, ex.plan)
    if (check.get("inputs") or {}).get("states") == "selected" and not states:
        ex.account(check, arm, "skipped-user", None,
                   "this check evaluates a world state other than the baseline, and the plan's "
                   "--states selected none: " + ", ".join(ex.plan["states"]["selected"])
                   + ". An unexplored state is recorded, never assumed equal to the baseline.")
        return

    out_dir = ex.run / "outputs" / check["checkId"]
    out_dir.mkdir(parents=True, exist_ok=True)
    emit = out_dir / f"{arm['armId']}.json"

    raw = list(adapter.get("args") or [])
    args: list[str] = []
    i = 0
    while i < len(raw):
        tok = raw[i]
        if tok == "{target}":
            targets = [str(t.get("name")) for t in (ex.plan.get("targets") or [])
                       if isinstance(t, dict) and t.get("name")]
            if not targets:
                ex.account(check, arm, "blocked(no-target)", None,
                           "the plan names no target plugin, so this probe cannot be told what to read")
                return
            # The SHAPE the registry wrote decides: `--target {target}` is a flag and repeats with
            # its flag; a bare `{target}` is POSITIONAL and repeats on its own. Handing a positional
            # probe a `--target` is how blockprobe was told to open a plugin called "--target".
            flag = args[-1] if args and args[-1].startswith("--") else None
            if flag:
                args.pop()
                for n in targets:
                    args += [flag, n]
            else:
                args += targets
            i += 1
            continue
        if tok == "{nexusId}":
            nid = plan_nexus_id(ex.plan)
            if not nid:
                ex.account(check, arm, "blocked(no-nexus-id)", None,
                           "the scope records no Nexus id for any target (the mod folder's meta.ini "
                           "carries none), so there is no page to read offline")
                return
            args.append(str(nid))
            i += 1
            continue
        if tok == "{switchMap}":
            e = ex.ledger.entry("state.switch-census", arm["armId"], None)
            r = load_json(ex.run / "receipts" / f"state.switch-census__{arm['armId']}.json")
            path = None
            if e and e.get("status") in TERMINAL_OK and r and r["validation"]["accepted"]:
                outs = r.get("outputs") or []
                path = str(ex.run / outs[0]["path"]) if outs and outs[0].get("path") else None
            if not path:
                ex.account(check, arm, "blocked(no-switch-map)", None,
                           "no accepted state.switch-census receipt on this arm, so there is no "
                           "switch map for this check to read")
                return
            args.append(path)
            i += 1
            continue
        if tok == "{pair}":
            # The BASELINE arm is the reference: what a patch would have to reconcile is what every
            # OTHER arm moves, so the baseline runs once with the plan-wide set. A candidate or
            # supplied arm runs with its OWN pairs - the moves that arm actually makes.
            pairs = all_plan_pairs(ex) if arm["kind"] == "baseline" else target_pairs(ex, arm)
            if not pairs and arm["kind"] != "baseline":
                pairs = all_plan_pairs(ex)
            if not pairs:
                ex.account(check, arm, "blocked(no-pair-source)", None,
                           "the candidate manifest records no move involving a target plugin, so "
                           "there is no plugin pair for this check to verify")
                return
            flag = args.pop() if args and args[-1].startswith("--") else "--pair"
            for a_, b_ in pairs:
                args += [flag, a_, b_]
            i += 1
            continue
        subs = {"order": arm["candidatePath"], "emit": str(emit), "json": str(emit),
                "out": str(out_dir), "run": str(ex.run), "arm": arm["armId"]}
        for k, v in subs.items():
            tok = tok.replace("{" + k + "}", str(v))
        args.append(tok)
        i += 1

    flag = adapter.get("statesFlag")
    if states and flag:
        for st in states:
            args += [flag, str(st)]

    argv = list(prog)
    if kind in ("scopeprobe", "probe-emit", "probe-json") and "--order" not in args and arm["candidatePath"]:
        argv += ["--order", arm["candidatePath"]]
    if kind == "scopeprobe" and "--target" not in args:
        # scopeprobe reads WHAT A TARGET CONTAINS; without a target it has nothing to open and
        # refuses. The target list is the plan's, named, never guessed from the order.
        targets = [str(t.get("name")) for t in (ex.plan.get("targets") or [])
                   if isinstance(t, dict) and t.get("name")]
        if not targets:
            ex.account(check, arm, "blocked(no-target)", None,
                       "the plan names no target plugin, so the footprint probe has nothing to open")
            return
        for n in targets:
            argv += ["--target", n]
    argv += args
    out_kind = adapter.get("output") or "conflict-file"
    if out_kind == "conflict-file" and "--emit" not in argv:
        argv += ["--emit", str(emit)]
    elif out_kind == "json" and "--json" not in argv and "--out" not in argv:
        # Only when the probe's own flag shape takes one. A probe told to write into --out DIR
        # (adviceprobe) has no --json at all, and appending one would be a refused argument.
        argv += ["--json", str(emit)]
    if wants_common_flags(adapter):
        argv += ex.common_flags()
    if ex.opts.get("probeArgs"):
        argv += ex.opts["probeArgs"].split()

    if kind == "adviceprobe":
        if "--refresh" in argv:
            i = argv.index("--refresh")
            word = argv[i + 1] if i + 1 < len(argv) else ""
            if word != "offline":
                ex.account(check, arm, "blocked(network-refused)", None,
                           f"the registry adapter asks for --refresh {word!r}; evalrun never fetches. "
                           f"Use --refresh offline, or run adviceprobe yourself and supply its output.")
                return
        elif "--offline" not in argv:
            argv += ["--offline"]

    # Where the RESULT actually lands: the file the probe writes, not a file we named for it.
    outputs = [emit]
    if out_kind == "text":
        # A text probe prints its answer; the child's LOG is the output, and demanding an emit path
        # from it would reject every run it ever makes. run_child records the log in outputs[].
        outputs = []
    elif "--out" in argv and out_kind == "json":
        nid = plan_nexus_id(ex.plan)
        named = out_dir / f"report-{nid}.json" if nid else None
        if named is not None:
            outputs = [named]

    ex.run_child(check, arm, None, argv, outputs, states=states)


def candidate_stale(arm: dict, memo: "HashMemo | None" = None) -> str | None:
    """None when the LIVE candidate file still hashes to the plan's frozen value, else why not. The
    order a child is about to be launched on is the file on disk now, not the copy the run assembled
    earlier, so this is checked before every launch."""
    path = arm.get("candidatePath")
    want = arm.get("candidateSha256")
    if not path or not is_sha256(want):
        return None
    p = Path(path)
    nxt = "Next action: plan again."
    if not p.exists():
        return (f"the plan's candidate file {p} is gone, so the order this arm names cannot be read. {nxt}")
    got = (memo or HashMemo()).sha256(p)
    if got != want:
        return (f"{arm.get('candidateFile')} at {p} now hashes {str(got)[:12]}; the plan froze "
                f"{str(want)[:12]}. The order this arm would be measured on is not the one the plan "
                f"approved. {nxt}")
    return None


def decided_on_arm(ex: Executor, pre: str, arm_id: str) -> str | None:
    """The arm's own status for prerequisite `pre` when that status decides for this arm alone, else None.
    A prerequisite MEASURED on this arm and failed there (`rejected(...)` or `failed`) decides: a
    masters-and-order refused on this arm is not settled by the baseline's accepted one. So does one
    `blocked(prerequisite:...)` on this arm - one hop down from such a failure (a switch census blocked by the
    arm's refused order does not borrow the baseline's census); a prerequisite unsettled on the baseline as
    well blocks either way. Anything else - no entry, or one not attempted (held back, blocked before launch
    for another reason) - leaves the baseline's answer counting."""
    own = str((ex.ledger.entry(pre, arm_id, None) or {}).get("status") or "")
    return own if (own.startswith("rejected") or own == "failed" or own.startswith("blocked(prerequisite:")) \
        else None


def prereq_answer_arms(ex: Executor, pre: str, arm_id: str) -> tuple[str, ...]:
    """The arms whose ledger entry answers prerequisite `pre` for `arm_id` (`decided_on_arm`)."""
    return (arm_id,) if decided_on_arm(ex, pre, arm_id) else (arm_id, "baseline")


def prereq_block_detail(ex: Executor, pre: str, arm_id: str) -> str:
    own = decided_on_arm(ex, pre, arm_id)
    if own:
        return (f"the prerequisite {pre} is {own} on this arm, which decides for this arm: the baseline's "
                "answer does not settle it")
    return (f"the prerequisite {pre} has no accepted receipt on this arm"
            + ("" if arm_id == "baseline" else " (or on the baseline every arm is measured against)"))


def uncredited_import_still_blocked(ex: Executor, check: dict, arm_id: str) -> bool:
    """True when this mcp-only entry is `blocked(prerequisite:...)` - an import the lifecycle gate stopped crediting -
    and `prereq_satisfied` still names a prerequisite for the arm."""
    own = str((ex.ledger.entry(check["checkId"], arm_id, None) or {}).get("status") or "")
    return own.startswith("blocked(prerequisite:") and prereq_satisfied(ex, check, arm_id) is not None


def uncredited_import_cause(ex: Executor, check: dict, arm_id: str) -> str | None:
    """The status word of an mcp-only entry that `resume` stopped crediting because its inputs could not be
    re-checked - `invalidated(...)` or `refused-reuse(...)`. Kept rather than flattened to `mcp-only`, which would
    read as "nothing was ever imported here"."""
    own = str((ex.ledger.entry(check["checkId"], arm_id, None) or {}).get("status") or "")
    return own if own.startswith(("invalidated(", "refused-reuse(")) else None


def superseded_import(ex: Executor, check: dict, arm: dict) -> dict | None:
    """`{"supersededImport": {...}}` when the receipt an accounting receipt is about to replace is an ACCEPTED import,
    so its origin does not vanish with it: the run still says an import was made, where its copied evidence is, and
    what it claimed. A receipt that ALREADY carries a superseded record hands it on, so the record survives the
    second and every later accounting pass over the same row rather than only the first (review R2a M1). None when
    neither is there."""
    key = Ledger.key(check["checkId"], arm["armId"], None)
    rc = load_json(ex.run / "receipts" / f"{key}.json")
    if not isinstance(rc, dict):
        return None
    imported = rc.get("imported")
    if not isinstance(imported, dict) or not (rc.get("validation") or {}).get("accepted"):
        carried = rc.get("supersededImport")
        return {"supersededImport": carried} if isinstance(carried, dict) else None
    # The folders of THIS key only: `<key>` and its re-imports `<key>~<n>`. A prefix glob also matched an arm whose
    # id starts with this one (review R2a L1 / R2b F2), which would point a reader at another arm's evidence.
    folders = [rel_to(ex.run, p) for p in sorted((ex.run / "imports").glob(f"{key}*"))
               if p.is_dir() and (p.name == key or p.name.startswith(key + "~"))]
    return {"supersededImport": {"receiptId": rc.get("receiptId"), "origin": imported.get("origin") or "unknown",
                                 "reusedFrom": imported.get("reusedFrom"),
                                 "sourceSha256": imported.get("sourceSha256"),
                                 "capturedUtc": imported.get("capturedUtc"), "folders": folders,
                                 "note": "this check was credited by an import that is no longer credited; the "
                                         "status and cause on this receipt say why"}}


def reuse_blocked_by(ex: Executor, check: dict, arm_id: str) -> str | None:
    """The first prerequisite whose own status on this arm now decides against an entry accepted earlier
    (typically on the baseline's answer, before the arm's own prerequisite was measured). Such an entry is
    blocked, not reused: reuse must not keep what the prerequisite gate would refuse to launch."""
    return next((p for p in check.get("prerequisites") or [] if decided_on_arm(ex, p, arm_id)), None)


def prereq_satisfied(ex: Executor, check: dict, arm_id: str) -> str | None:
    """None when every prerequisite of this check is settled on this arm, else the first that is
    not. A `not-applicable` prerequisite counts as satisfied: it is an answer, not an absence. Which
    arms answer is `prereq_answer_arms`."""
    for pre in check.get("prerequisites") or []:
        ok = False
        for aid in prereq_answer_arms(ex, pre, arm_id):
            e = ex.ledger.entry(pre, aid, None)
            if e and (e.get("status") in TERMINAL_OK
                      or str(e.get("status") or "").startswith("not-applicable")):
                ok = True
                break
        if not ok:
            return pre
    return None


def sweep_arms_allowed(ex: Executor, sweep_checks: list[dict]) -> dict:
    """Which arms may enter the sweep at all. A prerequisite gates the sweep exactly as it gates
    every other check: an arm whose prerequisite is not settled is kept OUT of the candidate
    directory, and if the BASELINE's prerequisite is not settled the sweep does not run - every
    other arm is measured against that baseline, so an unverified baseline makes the whole sweep
    an unverified comparison rather than a partial one."""
    allowed, blocked = {}, {}
    for c in sweep_checks:
        for r in c["arms"]:
            miss = prereq_satisfied(ex, c, r["armId"])
            if miss:
                blocked.setdefault(r["armId"], miss)
    base_block = blocked.get("baseline")
    for c in sweep_checks:
        for r in c["arms"]:
            if base_block:
                allowed[r["armId"]] = f"blocked:{base_block}"
            elif r["armId"] in blocked:
                allowed[r["armId"]] = f"blocked:{blocked[r['armId']]}"
            else:
                allowed[r["armId"]] = "ok"
    return allowed


SWEEP_FOLDER_RE = re.compile(r"^inv-(\d{4,})$")


def sweep_launch_folders(run: Path) -> list[Path]:
    """`RUN/sweep/inv-NNNN` folders, oldest first. One per ordersweep launch; files an older layout left directly
    under `RUN/sweep` are not a launch folder and are never read for credit or reuse."""
    root = Path(run) / "sweep"
    if not root.is_dir():
        return []
    found = []
    for p in root.iterdir():
        m = SWEEP_FOLDER_RE.match(p.name)
        if m and p.is_dir():
            found.append((int(m.group(1)), p))
    return [p for _, p in sorted(found)]


def new_sweep_folder(run: Path) -> Path:
    """A folder no earlier launch wrote: the next number, created exclusively."""
    folders = sweep_launch_folders(run)
    n = int(SWEEP_FOLDER_RE.match(folders[-1].name).group(1)) + 1 if folders else 1
    root = Path(run) / "sweep"
    root.mkdir(parents=True, exist_ok=True)
    while True:
        p = root / f"inv-{n:04d}"
        try:
            p.mkdir()
            return p
        except FileExistsError:
            n += 1


def read_sweep_jobs(inv: Path) -> dict[str, dict]:
    """job id -> the record ordersweep appended for it in this launch's `jobs.jsonl`. A line that does not parse
    credits nothing: its job then has no record, which the split names."""
    out: dict[str, dict] = {}
    p = Path(inv) / "jobs.jsonl"
    if p.exists():
        for ln in p.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                rec = json.loads(ln)
            except ValueError:
                continue
            if isinstance(rec, dict) and rec.get("job"):
                out[str(rec["job"])] = rec
    return out


def run_ordersweep(ex: Executor, sweep_checks: list[dict], parent, allowed: dict | None = None) -> None:
    """Compose ordersweep.py ONCE, into a folder of its own, then split what that launch recorded into
    per-(check, arm, part) receipts. Its classification and credit rules are consumed verbatim, never
    re-implemented."""
    plan, run = ex.plan, ex.run
    cdir = run / "candidates"
    cdir.mkdir(parents=True, exist_ok=True)

    allowed = allowed or {}
    account_sweep_kept_out(ex, sweep_checks, allowed)
    runnable = {a for a, v in allowed.items() if v == "ok"} if allowed else None
    if runnable is not None and "baseline" not in runnable:
        print("   ordersweep: not run - the baseline's prerequisite is not settled")
        return

    # assemble RUN/candidates, hash-checked against the plan — the LIVE file, not only the copy this
    # run assembled earlier: a copy that still matches the plan says nothing about the file the plan
    # points at, and the sweep would otherwise measure a stale copy of a changed order.
    baseline_id = next(a["armId"] for a in plan["arms"] if a["kind"] == "baseline")
    memo = HashMemo()
    stale_arms = set()
    # An arm no swept check plans (held back by --max-candidates, payload missing, ...) is never handed to
    # ordersweep: it would be measured and credited while the plan says it was not evaluated.
    planned_arms = {r["armId"] for c in sweep_checks for r in c["arms"] if r.get("status") == "planned"}
    for arm in plan["arms"]:
        if (runnable is not None and arm["armId"] not in runnable) or \
                (arm["armId"] not in planned_arms and arm["kind"] != "baseline"):
            if arm.get("candidateFile"):
                # a copy an earlier invocation assembled would hand ordersweep an arm this one keeps out
                (cdir / arm["candidateFile"]).unlink(missing_ok=True)
            continue
        src = Path(arm["candidatePath"])
        dst = cdir / arm["candidateFile"]
        if not src.exists():
            for check in sweep_checks:
                ex.account(check, arm, "blocked(candidate-missing)", "output-missing",
                           f"the plan's candidate file {src} is gone")
            stale_arms.add(arm["armId"])
            dst.unlink(missing_ok=True)
            continue
        why = candidate_stale(arm, memo)
        if why:
            for check in sweep_checks:
                ex.account(check, arm, "blocked(candidate-changed)", "stale-inputs", why)
            stale_arms.add(arm["armId"])
            dst.unlink(missing_ok=True)  # never hand ordersweep an order the plan did not approve
            continue
        if not dst.exists() or sha256_file(dst) != arm["candidateSha256"]:
            shutil.copy2(src, dst)
        if sha256_file(dst) != arm["candidateSha256"]:
            for check in sweep_checks:
                ex.account(check, arm, "blocked(candidate-changed)", "stale-inputs",
                           f"{arm['candidateFile']} no longer hashes to the plan's value")
            stale_arms.add(arm["armId"])
            dst.unlink(missing_ok=True)
            continue
    if baseline_id in stale_arms:
        print("   ordersweep: not run - the baseline candidate file no longer hashes to the plan's value")
        return
    if stale_arms:
        runnable = (runnable if runnable is not None
                    else {a["armId"] for a in plan["arms"]}) - stale_arms
    src_manifest = Path(plan["candidatesDir"]) / "candidates.json"
    if src_manifest.exists():
        shutil.copy2(src_manifest, cdir / "candidates.json")

    # A folder of this launch's own: nothing an earlier launch wrote is at any path this child writes, and no later
    # launch writes here, so a receipt's files stay the files it was validated from. Earlier launches' caches are
    # passed read-only, newest first; ordersweep reuses an emit from one only after re-hashing it.
    earlier = sweep_launch_folders(run)
    inv = new_sweep_folder(run)
    folder = rel_to(run, inv)
    rows = ex.ledger.doc.get("runIds") or []
    if rows:
        rows[-1].setdefault("sweepFolders", []).append(folder)
        ex.ledger.save()
    probes = sorted({c["adapter"]["exe"] for c in sweep_checks if c["adapter"].get("exe")})
    argv = [sys.executable, str(HERE / "ordersweep.py"),
            "--candidates", str(cdir), "--out", str(inv),
            "--probes", ",".join(probes), "--harness", str(ex.harness)]
    baseline_file = next(a["candidateFile"] for a in plan["arms"] if a["kind"] == "baseline")
    argv += ["--baseline", str(cdir / baseline_file)]
    argv += ex.common_flags()
    if ex.opts.get("probeArgs"):
        argv += ["--probe-args", ex.opts["probeArgs"]]
    if ex.opts.get("diffTolerance"):
        argv += ["--diff-tolerance", str(ex.opts["diffTolerance"])]
    if ex.opts.get("noReverse"):
        argv += ["--no-reverse"]
    for name, prefix in (ex.opts.get("probeCmd") or {}).items():
        argv += ["--probe-cmd", f"{name}={prefix}"]
    for f in reversed(earlier):
        if (f / "cache.json").exists():
            argv += ["--reuse-cache", str(f / "cache.json")]

    for check in sweep_checks:
        for arm_row in check["arms"]:
            if arm_row["armId"] in stale_arms or (runnable is not None and arm_row["armId"] not in runnable) \
                    or arm_row.get("status") != "planned":
                # Kept out of the sweep: it is not being measured, so it is never marked `running` - a stop would
                # otherwise leave it running, later healed to a wrong cause.
                continue
            e = ex.ledger.entry(check["checkId"], arm_row["armId"], "arm")
            if e and e.get("status") in TERMINAL_OK:
                # An arm ordersweep will serve from its own cache is not being re-measured. Marking it
                # `running` turned reused work into a fresh `completed` with a new receipt, which is a
                # new measurement claim for a child that never ran.
                continue
            ex.ledger.transition(check["checkId"], arm_row["armId"], "arm", "running")
    if ex.deadline is not None:
        # the sweep's probes get the tree rule too: ordersweep caches a probe only once its tree is empty
        argv += ["--deadline-seconds", f"{max(0.0, ex.deadline - time.perf_counter()):.3f}"]
    started_iso, started_mono = utcnow(), time.time()
    res = ex.runner.run([{"id": "ordersweep", "argv": argv, "env": ex.child_env()}])[0]
    log = ex.logs / "ordersweep.txt"
    if res.get("stopped") in ("before-launch", "uncontained") and not any(inv.iterdir()):
        # the child never ran, so there is no launch to keep: no folder, no recorded launch
        inv.rmdir()
        if rows and folder in (rows[-1].get("sweepFolders") or []):
            rows[-1]["sweepFolders"].remove(folder)
            if not rows[-1]["sweepFolders"]:
                rows[-1].pop("sweepFolders")
            ex.ledger.save()
    if res.get("stopped"):
        # Defect iv: the sweep was stopped at the deadline (or never launched). Nothing in its folder is read
        # for credit: the summary is absent, an emit may be written by a probe that never finished. Every planned
        # part not already terminal-ok is accounted with the named cause. Probe jobs whose process tree emptied
        # before the stop are carried only by that folder's identity-verified cache, which the next launch reads
        # (`--reuse-cache`): under a deadline ordersweep records a probe only after its whole tree is empty, so a
        # probe that exited 0 with a descendant still running is never cached and is re-measured.
        if res["stopped"] == "deadline":
            ex.children += 1
            ex.deadline_stops += 1
        for check in sweep_checks:
            for arm_row in check["arms"]:
                aid = arm_row["armId"]
                if aid in stale_arms or (runnable is not None and aid not in runnable):
                    parent.advance()  # already accounted blocked(...) above
                    continue
                arm = ex.arms[aid]
                for part in (("arm",) if arm["kind"] == "baseline" else SWEEP_PARTS):
                    e = ex.ledger.entry(check["checkId"], aid, part)
                    if e and e.get("status") in TERMINAL_OK:
                        ex.ledger.transition(check["checkId"], aid, part, "reused",
                                             receipt_id=e.get("receiptId"), detector=e.get("detector"),
                                             extra={"reusedFromRunId": e.get("runId")})
                        continue
                    # An arm the plan never meant to measure keeps its own cause: the deadline stopped nothing.
                    if account_unplanned_sweep_part(ex, check, arm, arm_row.get("status"), part):
                        continue
                    if part == "reverse" and ex.opts.get("noReverse"):
                        # no reverse was going to be measured, stop or no stop: the unstopped path's own entry
                        ex.account(check, arm, "blocked(no-reverse-measurement)", None,
                                   "no reverse emit: --no-reverse was given, so every created row reads "
                                   "unmeasured under the baseline order", part=part)
                        continue
                    ex.account_stop(check, arm, part, res, "the ordersweep child",
                                    log=log if log.exists() else None)
                parent.advance()
        return
    ex.children += 1
    ended_iso = utcnow()
    summary = load_json(inv / "summary.json") or {}
    jobs = read_sweep_jobs(inv)

    for check in sweep_checks:
        probe = check["adapter"]["exe"]
        # the baseline arm first: every candidate part is credited only on the baseline arm emit this split accepted
        for arm_row in sorted(check["arms"], key=lambda r: ex.arms[r["armId"]]["kind"] != "baseline"):
            if arm_row["armId"] in stale_arms or (runnable is not None and arm_row["armId"] not in runnable):
                parent.advance()  # already accounted blocked(...) above
                continue
            arm = ex.arms[arm_row["armId"]]
            if arm_row.get("status") != "planned":
                # kept out of the candidate directory above, so nothing of it was measured: its plan status is its cause
                for part in (("arm",) if arm["kind"] == "baseline" else SWEEP_PARTS):
                    account_unplanned_sweep_part(ex, check, arm, arm_row.get("status"), part)
                parent.advance()
                continue
            split_sweep_receipts(ex, check, arm, probe, inv, jobs, summary, res, started_iso,
                                 ended_iso, started_mono, log)
            parent.advance()


def split_sweep_receipts(ex: Executor, check: dict, arm: dict, probe: str, inv: Path, jobs: dict,
                         summary: dict, res: dict, started_iso: str, ended_iso: str,
                         started_mono: float, log: Path) -> None:
    """One (check, arm)'s parts, each decided on its own from THIS launch's job records (`jobs.jsonl`).

    The plan decides which receipts exist, not ordersweep's summary rows: an arm whose probe produced nothing has no
    summary row, and that is exactly the arm whose own cause and exit code must be written down. A part accepted by an
    earlier invocation stays `reused` - its receipt and files untouched - unless this launch produced different bytes
    for it; a part that is not accepted is decided from this launch's record for its job. Credit rests only on accepted
    emits: a candidate's parts on the same check's accepted baseline arm, a reverse and a diff on the accepted arm, a
    diff on the accepted reverse its created rows were split by. The caller settles the baseline arm first."""
    run = ex.run
    cid, aid = check["checkId"], arm["armId"]
    stem = Path(arm["candidateFile"]).stem
    is_base = arm["kind"] == "baseline"
    baseline_id = next(a["armId"] for a in ex.plan["arms"] if a["kind"] == "baseline")
    folder = rel_to(run, inv)
    cand = (summary.get("candidates", {}).get(stem, {}) or {})
    row = None
    for r in cand.get("rows", []):
        if r.get("probe") == probe:
            row = r
    classification = cand.get("classification")

    def text_of(p: Path | None) -> str:
        return p.read_text(encoding="utf-8", errors="replace") if p and p.exists() else ""

    def path_of(text) -> Path | None:
        return Path(text) if text else None

    def status_of(part: str, arm_id: str = aid) -> str:
        return str((ex.ledger.entry(cid, arm_id, part) or {}).get("status"))

    def accepted_receipt(part: str, arm_id: str = aid) -> dict | None:
        e = ex.ledger.entry(cid, arm_id, part)
        if not e or e.get("status") not in TERMINAL_OK:
            return None
        return load_json(run / "receipts" / f"{Ledger.key(cid, arm_id, part)}.json") or None

    def output_sha(receipt: dict | None) -> str | None:
        return next((o.get("sha256") for o in (receipt or {}).get("outputs") or []), None)

    def keep(part: str) -> None:
        e = ex.ledger.entry(cid, aid, part)
        if e and e.get("status") == "completed":
            ex.ledger.transition(cid, aid, part, "reused", receipt_id=e.get("receiptId"), detector=e.get("detector"),
                                 extra={"reusedFromRunId": e.get("runId")})

    def produced(rec: dict | None) -> bool:
        return bool(rec) and rec.get("exitCode") == 0 and (
            rec.get("outcome") == "reused" or (rec.get("outcome") == "ran" and bool(rec.get("emitWritten"))))

    def failed_upstream(job: str | None) -> str | None:
        """Why this launch ran nothing downstream of `job`: its record says it ran and produced no emit."""
        rec = jobs.get(job) if job else None
        if rec is None or produced(rec):
            return None
        how = (f"was stopped ({rec.get('stopped')})" if rec.get("outcome") == "stopped"
               else f"ran with exit {rec.get('exitCode')}")
        return f"this launch's job {job} {how} and produced no emit"

    def job_res(rec: dict | None) -> dict:
        # The receipt's exit code is the JOB's own, never ordersweep's (review V2 M4).
        return {"runId": res.get("runId"), "exitCode": (rec or {}).get("exitCode")}

    def sweep_block(job: str, rec: dict | None) -> dict:
        src = path_of((rec or {}).get("reusedFrom"))
        return {"sweep": {"folder": folder, "job": job, "outcome": (rec or {}).get("outcome"),
                          "reusedFrom": rel_to(run, src.parent) if src else None}}

    def account_stopped(part: str, job: str, rec: dict) -> None:
        # The same mapping evalrun's own children get (Executor.account_stop): a containment refusal reads
        # blocked(uncontained), a launch the deadline refused skipped-budget, a stop at the deadline stopped(deadline)
        # - never a budget stop for a job that could not be contained (review R2 M-1).
        stop = dict(rec.get("stop") or {}, stopped=rec.get("stopped"), runId=res.get("runId"))
        ex.account_stop(check, arm, part, stop, f"job {job} of the ordersweep child", log=path_of(rec.get("log")))

    def emit_part(part: str, job: str, default_emit: Path, base_expected: tuple[Path, str] | None,
                  upstream: str | None, gate: tuple[str, str] | None = None) -> tuple[Path, str] | None:
        """Settle the `arm` or `reverse` part from its job record. Returns the credited emit (path, sha256) or None.
        `base_expected` is the credited emit this job is measured against (None: nothing to match); `upstream` the job
        whose failure in this launch would explain a missing record; `gate` (status, why) says nothing this job measured
        may be credited: the part is still validated, so a failure keeps its own cause, and a result that would be
        accepted takes the gate's status instead."""
        rec = jobs.get(job)
        prior = accepted_receipt(part)
        if prior is not None and gate is None:
            base_ok = base_expected is None or (prior.get("consumed") or {}).get("baseEmitSha256") == base_expected[1]
            if base_ok and (not produced(rec) or rec.get("emitSha256") == output_sha(prior)):
                # Accepted earlier and nothing this launch did says otherwise: its receipt and files stand as written.
                keep(part)
                out0 = (prior.get("outputs") or [{}])[0]
                return run / out0["path"], out0.get("sha256")
        if rec is None:
            why = failed_upstream(upstream)
            if why:
                # Nothing ran for this part because the job it rests on failed in this launch: that job's failure is
                # the cause, named with its exit code (review R1 M2), never a missing record.
                ex.account(check, arm, "blocked(no-reverse-measurement)" if part == "reverse"
                           else "blocked(no-baseline-measurement)", "blocked-prerequisite",
                           f"no {part} job ran: {why}", part=part)
                return None
            v = {"accepted": False, "detector": None, "findings": None, "coverage": [], "verdict": None,
                 "identity": None,
                 "rejections": [{"cause": "completion-missing",
                                 "detail": f"the ordersweep child (exit {res.get('exitCode')}) recorded no result for "
                                           f"job {job} in {folder}/jobs.jsonl: nothing of that job is credited"}]}
            ex.finish(check, arm, part, check["adapter"], None, job_res(rec), started_iso, ended_iso,
                      [default_emit], log, v, extra=sweep_block(job, rec))
            return None
        if rec.get("outcome") == "stopped":
            account_stopped(part, job, rec)
            return None
        emit = path_of(rec.get("emit")) or default_emit
        base_path = path_of((rec.get("inputs") or {}).get("base"))
        reused = rec.get("outcome") == "reused"
        job_log = path_of(rec.get("log"))
        src_log = (path_of(rec.get("reusedFrom")).parent / "logs" / f"{job}.txt") if reused and rec.get("reusedFrom") \
            else None
        v = ex.validator.validate(check=check, arm=arm, part=part, adapter=check["adapter"],
                                  exit_code=rec.get("exitCode"), log_text=text_of(src_log if reused else job_log),
                                  outputs=[emit], pre_hashes={}, started=None if reused else started_mono)
        if part == "reverse" and v["accepted"] and v["detector"] is None and v["findings"] is not None:
            v["detector"] = "finding" if v["findings"] > 0 else "no-finding"
        if gate and v["accepted"]:
            ex.account(check, arm, gate[0], "blocked-prerequisite",
                       f"job {job} measured on {gate[1]}: nothing measured on an emit that was not accepted is credited",
                       part=part)
            return None
        # The reverse arm is measured on the BASELINE order with the candidate as --base, so its own Configuration IS
        # the baseline's — which is why D2 validates it against the BASELINE arm's frozen request
        # (Validator.reference_arm) instead of exempting it from the identity check.
        receipt_log = (inv / "cache-log.txt") if reused else job_log
        receipt = ex.finish(check, arm, part, check["adapter"], None, job_res(rec), started_iso, ended_iso,
                            [emit], receipt_log if receipt_log and receipt_log.exists() else log, v,
                            base_emit=base_path, extra=sweep_block(job, rec))
        return (emit, output_sha(receipt)) if v["accepted"] else None

    # ---- the arm part
    base_job = f"{probe}-baseline"
    arm_job = base_job if is_base else f"{probe}-{stem}"
    base_credit = None
    base_gate = None
    if not is_base:
        br = accepted_receipt("arm", baseline_id)
        if br is not None:
            out0 = (br.get("outputs") or [{}])[0]
            base_credit = (run / out0["path"], out0.get("sha256"))
        else:
            # Review R1 H1: every part of a candidate arm is measured against the baseline arm's emit, so none is
            # credited while that emit is not accepted.
            base_gate = ("blocked(baseline-not-accepted)",
                         f"this check's baseline arm emit, and the baseline arm part is {status_of('arm', baseline_id)}")
    arm_credit = emit_part("arm", arm_job, inv / probe / ("baseline.json" if is_base else f"{stem}.json"),
                           base_credit, None if is_base else base_job, gate=base_gate)
    if is_base:
        return

    # ---- the reverse part
    rev_job = f"{probe}-{stem}-reverse"
    rev_credit = None
    if ex.opts.get("noReverse"):
        ex.account(check, arm, "blocked(no-reverse-measurement)", None,
                   "no reverse emit: --no-reverse was given, so every created row reads unmeasured under the "
                   "baseline order", part="reverse")
    elif arm_credit is None and jobs.get(rev_job) is None:
        ex.account(check, arm, "blocked(no-reverse-measurement)", None,
                   f"no reverse was measured: the arm part is {status_of('arm')}, so no reverse ran on it and every "
                   f"created row reads unmeasured under the baseline order", part="reverse")
    elif arm_credit is None:
        emit_part("reverse", rev_job, inv / probe / f"{stem}.reverse.json", None, arm_job,
                  gate=base_gate or ("blocked(arm-not-accepted)", f"the arm emit, and the arm part is {status_of('arm')}"))
    else:
        rev_credit = emit_part("reverse", rev_job, inv / probe / f"{stem}.reverse.json", arm_credit, arm_job)

    # ---- the diff part
    diff_job = f"diff-{probe}-{stem}"
    drec = jobs.get(diff_job)
    dinputs = (drec or {}).get("inputs") or {}
    prior = accepted_receipt("diff")
    if prior is not None and arm_credit is not None and base_credit is not None:
        pin = {r.get("part"): r.get("sha256") for r in (prior.get("consumed") or {}).get("inputs") or []}
        if pin.get("arm") == arm_credit[1] and pin.get("reverse") == (rev_credit[1] if rev_credit else None):
            # 3b: the diff this launch may have re-run lands in this launch's folder; the accepted receipt keeps
            # pointing at its own log, which no later launch writes.
            keep("diff")
            return
    diff_log = path_of((drec or {}).get("emit")) or inv / "logs" / f"{diff_job}.txt"
    paths = {k: path_of(dinputs.get(k)) for k in ("arm", "reverse")}
    diff = (row or {}).get("diff") or {}
    created = (row or {}).get("created") or {}
    verdict = diff.get("verdict")
    detector = None
    rejections = []
    if drec is None:
        why = failed_upstream(arm_job) or failed_upstream(base_job)
        if why or arm_credit is None:
            rejections.append({"cause": "output-missing",
                               "detail": f"{rel_to(run, diff_log)}: no diff ran, because "
                                         + (why or f"the arm part is {status_of('arm')}")})
        else:
            rejections.append({"cause": "completion-missing",
                               "detail": f"the ordersweep child (exit {res.get('exitCode')}) recorded no result for "
                                         f"job {diff_job} in {folder}/jobs.jsonl"})
    elif drec.get("outcome") == "stopped":
        account_stopped("diff", diff_job, drec)
        return
    elif drec.get("exitCode") not in (0, None):
        # conflictdiff exits 3 when it refuses a comparison (populations, states or configurations differ) and 2 on a
        # usage or read error, before any verdict: its own refusal line is the cause, not a missing verdict (R2 M-3)
        said = conflictdiff_refusal(text_of(diff_log))
        rejections.append({"cause": "exit-nonzero",
                           "detail": f"exit code {drec.get('exitCode')}: conflictdiff did not compare these emits"
                                     + (f" - it said {said[:REFUSAL_QUOTE_CHARS]!r}" if said else "")})
    elif not diff_log.exists() or not drec.get("emitWritten"):
        rejections.append({"cause": "output-missing", "detail": rel_to(run, diff_log)})
    elif row is None:
        rejections.append({"cause": "completion-missing",
                           "detail": f"the ordersweep child (exit {res.get('exitCode')}) wrote no summary row for "
                                     f"{probe}/{stem} in {folder}/summary.json, which is where this diff's verdict and "
                                     f"created rows are read"})
    elif verdict in (None, "(not run)", "(no verdict line)"):
        rejections.append({"cause": "schema-unsupported",
                           "detail": f"conflictdiff produced no verdict line for {probe}/{stem}"})
    else:
        if verdict == "DIFFERENTIAL-UNJUDGED" or diff.get("counts", {}).get("unmeasured", 0) > 0 \
                or created.get("unmeasured", 0) > 0:
            detector = "inconclusive"
        elif created.get("newDefect", 0) > 0 or diff.get("counts", {}).get("created", 0) > 0:
            detector = "finding"
        else:
            detector = "no-finding"
    if not rejections:
        # Its own checks pass; it is credited only on accepted emits (a gate, never a new measurement).
        gate = base_gate or (("blocked(arm-not-accepted)", f"the arm emit, and the arm part is {status_of('arm')}")
                             if arm_credit is None else
                             ("blocked(reverse-not-accepted)", f"a reverse emit that split its created rows, and the "
                                                               f"reverse part is {status_of('reverse')}")
                             if dinputs.get("reverse") and rev_credit is None else None)
        if gate:
            ex.account(check, arm, gate[0], "blocked-prerequisite",
                       f"job {diff_job} compared {gate[1]}: nothing measured on an emit that was not accepted is "
                       f"credited", part="diff")
            return
    vd = {"accepted": not rejections, "rejections": rejections, "detector": detector,
          "findings": diff.get("counts", {}).get("created"), "coverage": [], "verdict": verdict,
          "identity": None}
    # The emits the diff's result was read from, as the job recorded them: conflictdiff compared base and arm, and
    # the created rows were split by the reverse emit when one was measured (no reverse row when none was).
    diff_inputs = [{"part": part, "path": rel_to(run, p), "sha256": sha256_file(p)}
                   for part, p in (("arm", paths["arm"]), ("reverse", paths["reverse"])) if p is not None]
    ex.finish(check, arm, "diff", check["adapter"], None, job_res(drec), started_iso, ended_iso,
              [diff_log], diff_log if diff_log.exists() else log, vd, verdict=verdict,
              inputs=diff_inputs,
              extra={**sweep_block(diff_job, drec),
                     "orderEvaluation": {"classification": classification, "probes": [probe],
                                         "diff": diff, "created": created,
                                         "note": "ordersweep's own classification, verbatim and scoped "
                                                 "to the probes named; never a whole-evaluation pass"}})


def order_classification(run: Path, arm: dict, rows: list[dict]) -> dict:
    """ordersweep's classification word for an arm, only from ONE launch that measured every accepted diff it rests on.

    ordersweep classifies a candidate over all probes of one launch, and a resume keeps accepted diffs from earlier
    launches (§4.4), so the word a kept diff receipt carries can be an earlier launch's. The newest launch among the
    accepted diff receipts supplies the word only when its own job records and summary show it measured every one of
    them again - the same arm and reverse emits (sha256) and the same diff and created rows. Otherwise there is no word:
    the note says why and every launch's word is kept as historical. The word itself is ordersweep's, never recomputed.
    Receipts from before per-launch folders (no `sweep` block) give their word only when they all carry the same one."""
    rows = [r for r in rows if r.get("orderEvaluation")]
    if not rows:
        return {"classification": None}
    history = sorted({((r.get("sweep") or {}).get("folder"), (r.get("orderEvaluation") or {}).get("classification"))
                      for r in rows}, key=lambda x: str(x[0]))
    historical = [{"launch": f, "classification": c,
                   "probes": sorted({p for r in rows if (r.get("sweep") or {}).get("folder") == f
                                     and (r.get("orderEvaluation") or {}).get("classification") == c
                                     for p in (r.get("orderEvaluation") or {}).get("probes") or []})}
                  for f, c in history]
    launches = sorted({f for f, _ in history if f and SWEEP_FOLDER_RE.match(Path(f).name)},
                      key=lambda f: int(Path(f).name.split("-", 1)[1]))
    if not launches:
        words = {c for _, c in history}
        if len(words) == 1:
            return {"classification": words.pop()}
        return {"classification": None, "historicalClassifications": historical,
                "classificationNote": "the accepted diffs carry different ordersweep classifications and no launch "
                                      "record shows one measurement of all of them, so none is given"}
    newest = launches[-1]
    folder = Path(run) / newest
    stem = Path(arm["candidateFile"]).stem
    summary = load_json(folder / "summary.json") or {}
    cand = (summary.get("candidates") or {}).get(stem) or {}
    jobs = read_sweep_jobs(folder)
    memo = HashMemo()
    unconfirmed = []
    for r in rows:
        oe = r["orderEvaluation"]
        for probe in oe.get("probes") or []:
            rec = jobs.get(f"diff-{probe}-{stem}") or {}
            row = next((x for x in cand.get("rows") or [] if x.get("probe") == probe), None)
            want = {x.get("part"): x.get("sha256") for x in (r.get("consumed") or {}).get("inputs") or []}
            got = {k: memo.sha256(Path(v)) for k, v in (rec.get("inputs") or {}).items() if k in ("arm", "reverse") and v}
            same = (rec.get("outcome") == "ran" and rec.get("exitCode") == 0 and rec.get("emitWritten")
                    and got.get("arm") == want.get("arm") and got.get("reverse") == want.get("reverse")
                    and row is not None and row.get("diff") == oe.get("diff") and row.get("created") == oe.get("created"))
            if not same:
                unconfirmed.append(f"{probe} (accepted from {(r.get('sweep') or {}).get('folder') or 'an older layout'})")
    if not unconfirmed and cand.get("classification"):
        return {"classification": cand["classification"],
                "classificationSource": {"launch": newest,
                                         "note": "the newest launch among the accepted diffs measured every one of them "
                                                 "again with the same emits and the same rows"},
                **({"historicalClassifications": historical} if len(history) > 1 else {})}
    return {"classification": None, "historicalClassifications": historical,
            "classificationNote": f"no single launch measured every accepted diff of this arm (not in {newest}: "
                                  + ", ".join(unconfirmed or ["its summary carries no classification"])
                                  + "), so ordersweep's word for one launch would describe other evidence; the per-probe "
                                    "numbers stand"}


def run_derived(ex: Executor, check: dict, arm: dict) -> None:
    """A derived check computes from other receipts; it runs no child and invents no measurement."""
    adapter = check["adapter"]
    cid = check["checkId"]
    receipts = read_receipts(ex.run)
    derived_from = []
    detector = None
    findings = None
    payload = {}

    def blocked() -> bool:
        # §4.7: the prerequisite gate every other adapter path passes, applied once the derivation is
        # known to be one this controller computes. A derivation over an unsettled prerequisite reads
        # an absence as an input, and its `no-finding` would be counted as a clean check.
        pre = prereq_satisfied(ex, check, arm["armId"])
        if pre:
            ex.account(check, arm, f"blocked(prerequisite:{pre})", "blocked-prerequisite",
                       prereq_block_detail(ex, pre, arm["armId"]))
        return bool(pre)

    if adapter.get("derives") == "order-evaluation" or cid.endswith("order-evaluation"):
        if arm["kind"] == "baseline":
            # The baseline is the reference every other arm is measured AGAINST. There is no order
            # evaluation of it against itself, and pretending one exists would invent a result.
            ex.account(check, arm, "not-applicable", None,
                       "the baseline is the reference the other arms are measured against; an order "
                       "evaluation of the baseline against itself is not a measurement")
            return
        if blocked():
            return
        # Derived only from this arm's ACCEPTED diff parts: a diff part the sweep planned for this arm that is
        # rejected, stopped, blocked or absent would otherwise leave the evaluation reading clean over it.
        swept = [c["checkId"] for c in ex.plan["checks"]
                 if c["adapter"].get("kind") == "ordersweep-probe" and c.get("status") == "planned"
                 and any(r.get("armId") == arm["armId"] and r.get("status") == "planned" for r in c.get("arms") or [])]
        unsettled = [(c, (ex.ledger.entry(c, arm["armId"], "diff") or {}).get("status")) for c in swept
                     if (ex.ledger.entry(c, arm["armId"], "diff") or {}).get("status") not in TERMINAL_OK]
        if unsettled:
            ex.account(check, arm, f"blocked(prerequisite:{unsettled[0][0]})", "blocked-prerequisite",
                       "an order evaluation derives only from this arm's accepted diff parts; not accepted: "
                       + ", ".join(f"{c} diff ({s or 'no entry'})" for c, s in unsettled))
            return
        rows = [r for r in receipts.values()
                if r.get("armId") == arm["armId"] and r.get("part") == "diff"
                and r.get("orderEvaluation")]
        derived_from = [r["receiptId"] for r in rows]
        per_probe = {}
        word = order_classification(ex.run, arm, [r for r in rows if (r.get("validation") or {}).get("accepted")])
        classification = word["classification"]
        for r in rows:
            oe = r["orderEvaluation"]
            counts = (oe.get("diff") or {}).get("counts") or {}
            created = oe.get("created") or {}
            for p in oe.get("probes", []):
                # The NUMBERS, and only the numbers. Whether they amount to a regression is decided
                # in exactly one place - the finalizer - so a change to that rule cannot be made
                # true in one site and false in the other.
                per_probe[p] = {"verdict": r["result"].get("verdict"),
                                "counts": counts, "created": created,
                                "repaired": counts.get("repaired", 0),
                                "reclassified": counts.get("reclassified", 0),
                                "createdRows": created.get("created", 0),
                                "newDefect": created.get("newDefect", 0),
                                "attributed": created.get("attributed", 0),
                                "unmeasured": max(counts.get("unmeasured", 0),
                                                  created.get("unmeasured", 0))}
        payload = {**word, "perProbe": per_probe,
                   "scope": "the probes named above, in the population and state they ran in",
                   "note": "this receipt carries the per-probe NUMBERS only; whether they amount to a "
                           "regression is derived once, in the finalizer, from created.newDefect"}
        new_defect_probes = sum(1 for v in per_probe.values() if v["newDefect"] > 0)
        if not rows:
            detector = None
        elif new_defect_probes:
            detector = "finding"
        elif any(v["verdict"] in ("DIFFERENTIAL-UNJUDGED", None) for v in per_probe.values()):
            detector = "inconclusive"
        else:
            detector = "no-finding"
        findings = new_defect_probes

    elif adapter.get("derives") == "candidate-qualification" or cid.endswith("candidate-qualification"):
        if blocked():
            return
        evaluated: dict[str, str] = {}
        for r in receipts.values():
            if r["validation"]["accepted"]:
                for p in (r.get("consumed", {}).get("configuration", {}) or {}).get("plugins", []) or []:
                    if p.get("name") and p.get("sha256"):
                        evaluated[str(p["name"]).lower()] = p["sha256"]
        derived_from = [r["receiptId"] for r in receipts.values() if r["validation"]["accepted"]]
        manifest = load_json(ex.run / "candidates" / "candidates.json") or {}
        rows = []
        for arm2 in ex.plan["arms"]:
            for name in arm2.get("supplied", {}).get("declaredFiles", []) or []:
                key = Path(name).name.lower()
                rows.append({"file": Path(name).name, "armId": arm2["armId"],
                             "qualification": "qualified" if key in evaluated else "unqualified",
                             "why": ("its content hash is in an evaluated arm" if key in evaluated
                                     else "no evaluated arm consumed a plugin with this name: a "
                                          "downloadable hit stays a lead until its payload is measured")})
        for c in (manifest.get("Candidates") or []) if isinstance(manifest, dict) else []:
            f = Path(str(c.get("File", ""))).name
            if f:
                rows.append({"file": f, "armId": None, "qualification": "catalogued",
                             "why": "a candidate-order file, not a supplied patch payload"})
        payload = {"candidates": rows}
        findings = 0
        detector = "no-finding"

    else:
        ex.account(check, arm, "unsupported", None,
                   f"derived kind {adapter.get('derives')!r} is not one this controller computes "
                   f"(it computes order-evaluation and candidate-qualification)")
        return

    v = {"accepted": True, "rejections": [], "detector": detector, "findings": findings,
         "coverage": [], "verdict": None, "identity": None}
    ex.finish(check, arm, None, adapter, None, {"runId": None, "exitCode": 0}, utcnow(), utcnow(),
              [], None, v, extra={"derived": payload, "derivedFrom": derived_from})


def read_receipts(run: Path) -> dict[str, dict]:
    out = {}
    d = Path(run) / "receipts"
    if not d.exists():
        return out
    for p in sorted(d.glob("*.json")):
        doc = load_json(p)
        if isinstance(doc, dict):
            out[p.stem] = doc
    return out


# ------------------------------------------------------------------- resume and reuse (D6) --

def dependency_keys(plan: dict, receipts: dict, entry: dict) -> list[str]:
    """The ledger keys an entry's result RESTS ON — D5's edges, in one place, so `resume`, `finalize`
    and `bundle` cannot disagree about what follows what. `reverse` and `diff` follow the arm emit; a
    diff follows its reverse; every part of a non-baseline arm follows the same check's BASELINE arm,
    which is what it was measured against; a derived check follows every receipt it consumed; and any
    check follows its prerequisites on this arm and on the baseline."""
    baseline_id = next((a["armId"] for a in plan["arms"] if a["kind"] == "baseline"), "baseline")
    cid, aid, part = entry["checkId"], entry["armId"], entry.get("part")
    deps: list[str] = []
    if part in ("reverse", "diff"):
        deps.append(Ledger.key(cid, aid, "arm"))
    if part == "diff":
        deps.append(Ledger.key(cid, aid, "reverse"))
    if aid != baseline_id and part in ("arm", "reverse", "diff"):
        deps.append(Ledger.key(cid, baseline_id, "arm"))
    r = receipts.get(Ledger.key(cid, aid, part)) or {}
    for rid in (r.get("derivedFrom") or []):
        for k2, r2 in receipts.items():
            if r2.get("receiptId") == rid:
                deps.append(k2)
    check = next((c for c in plan["checks"] if c["checkId"] == cid), None)
    for pre in (check or {}).get("prerequisites", []):
        deps.append(Ledger.key(pre, aid, None))
        deps.append(Ledger.key(pre, baseline_id, None))
    return deps


def revalidate(run: Path, plan: dict, ledger: Ledger, ex: Executor) -> None:
    """Re-validate every completed/reused entry against disk, then invalidate transitively.

    D5's order per entry: the receipt gone, a foreign planId, an unverifiable input (a GAP — refused
    reuse), a changed input (invalidated), else reused. Every transition names its reason and the next
    action, and the hashing is memoised so 80 plugins across 60 receipts are read once each."""
    receipts = read_receipts(run)
    memo = HashMemo()
    invalidated: dict[str, str] = {}
    refused: dict[str, str] = {}
    baseline_id = next((a["armId"] for a in plan["arms"] if a["kind"] == "baseline"), "baseline")

    for k, e in list(ledger.doc["entries"].items()):
        if e.get("status") not in TERMINAL_OK:
            continue
        r = receipts.get(k)
        if r is None:
            invalidated[k] = "the receipt file is gone"
            continue
        if r.get("planId") != plan["planId"]:
            invalidated[k] = f"foreign-run: the receipt belongs to plan {r.get('planId')}"
            continue
        gap = reuse_gap(r)
        if gap:
            refused[k] = gap
            continue
        changed = changed_input(run, r, memo)
        if changed:
            invalidated[k] = changed

    # transitive: reverse and diff follow the arm emit; every part of a non-baseline arm follows the
    # BASELINE arm emit it was measured against (D4); derived follows what it consumed; a check
    # follows its prerequisites; everything follows the registry and the plan.
    changed_flag = True
    while changed_flag:
        changed_flag = False
        for k, e in list(ledger.doc["entries"].items()):
            if k in invalidated or k in refused or e.get("status") not in TERMINAL_OK:
                continue
            for d in dependency_keys(plan, receipts, e):
                if d in invalidated or d in refused:
                    invalidated[k] = f"an input it depends on was invalidated ({d})"
                    changed_flag = True
                    break

    for k, gap in refused.items():
        e = ledger.doc["entries"][k]
        ledger.transition(e["checkId"], e["armId"], e.get("part"), f"refused-reuse({gap})",
                          cause="stale-inputs", detail=gap,
                          next_action="re-run this check; an input it cannot state cannot be re-checked, "
                                      "so measured evidence is not reused on this entry")
    for k, why in invalidated.items():
        e = ledger.doc["entries"][k]
        ledger.transition(e["checkId"], e["armId"], e.get("part"), f"invalidated({why})",
                          cause="stale-inputs", detail=why,
                          next_action=("restore the named file and resume, or re-run this check to "
                                       "measure the inputs as they are now"))
    kept = [k for k, e in ledger.doc["entries"].items() if e.get("status") in TERMINAL_OK]
    for k in kept:
        e = ledger.doc["entries"][k]
        if e.get("status") == "completed":
            ledger.transition(e["checkId"], e["armId"], e.get("part"), "reused",
                              receipt_id=e.get("receiptId"), detector=e.get("detector"),
                              detail="every stated input still hashes to its recorded value",
                              next_action="nothing: this measurement stands",
                              extra={"reusedFromRunId": e.get("runId")})
    print(f"   resume: {len(kept)} receipt(s) reused, {len(invalidated)} invalidated, "
          f"{len(refused)} refused-reuse")


def reuse_gap(receipt: dict) -> str | None:
    """D5a: a receipt whose consumer cannot state an input is never reused, and EVERY unverifiable
    input is named — not the first one found. A gap is not a change: it is the admission that this
    entry's inputs cannot be re-checked at all, which is why reuse is refused rather than granted."""
    if not (receipt.get("validation") or {}).get("accepted"):
        return None
    c = receipt.get("consumed") or {}
    cfg = c.get("configuration") or {}
    gaps: list[str] = []
    if cfg.get("reuseGap"):
        gaps.append(str(cfg["reuseGap"]))
    plugins = cfg.get("plugins") or []
    if not plugins:
        gaps.append("the receipt names no consumed plugins, so what it read cannot be re-checked on disk")
    for p in plugins:
        name = p.get("name") or "(unnamed)"
        if not p.get("path"):
            gaps.append(f"consumed plugin {name} has no path")
        if not is_sha256(p.get("sha256")):
            gaps.append(f"consumed plugin {name} has no 64-hex sha256")
    for o in cfg.get("overlays") or []:
        if not o.get("path") or not is_sha256(o.get("sha256")):
            gaps.append(f"consumed overlay {o.get('kind') or '(unnamed)'} has no path or no sha256")
    for o in receipt.get("outputs") or []:
        if o.get("sha256") is None:
            gaps.append(f"output {o.get('path')} has no hash")
    tool = c.get("tool") or {}
    if tool.get("present") and not is_sha256(tool.get("dllSha256")):
        gaps.append(f"the tool {Path(str(tool.get('exe') or '?')).name} is present but its managed "
                    f"assembly has no recorded hash, so the code that ran cannot be identified")
    # A-9 (V's finding 2): a run-internal comparison input recorded WITHOUT a hash (the file was not
    # there when the receipt was written) is unverifiable, so reuse refuses it — the same answer
    # finalize's integrity walk gives, rather than a silent grant on one verb and a refusal on the other.
    if c.get("baseEmitPath") and not is_sha256(c.get("baseEmitSha256")):
        gaps.append(f"the base emit {c['baseEmitPath']} has no recorded hash")
    for row in c.get("inputs") or []:
        if row.get("path") and not is_sha256(row.get("sha256")):
            gaps.append(f"the {row.get('part') or 'comparison'} input {row['path']} has no recorded hash")
    return "; ".join(gaps) or None


def changed_input(run: Path, receipt: dict, memo: "HashMemo | None" = None) -> str | None:
    """D5b: what changed since this receipt was written, named with its path — and a file that is GONE
    is a change, not an absence of one. `exists() and hash-differs` treated a deleted plugin as "no
    change", which is how a receipt naming a file nobody could read any more stayed reusable.

    EVERY change is named, not the first found: when a re-run has already rewritten an output, the
    stale output is not the interesting half of the answer — the input that made it stale is."""
    memo = memo or HashMemo()
    changes: list[str] = []

    def verify(p: Path, recorded: str, what: str) -> None:
        if not Path(p).exists():
            changes.append(f"{what} is gone at {p}")
        elif memo.sha256(p) != recorded:
            changes.append(f"{what} changed on disk at {p}")

    for o in receipt.get("outputs") or []:
        if o.get("sha256") is not None:
            verify(Path(run) / o["path"], o["sha256"], f"output {o['path']}")
    c = receipt.get("consumed") or {}
    if c.get("candidatePath") and c.get("candidateSha256"):
        verify(Path(c["candidatePath"]), c["candidateSha256"],
               f"the candidate file {Path(c['candidatePath']).name}")
    for p in (c.get("configuration") or {}).get("plugins", []) or []:
        if not p.get("path") or not is_sha256(p.get("sha256")):
            continue  # unverifiable, not unchanged: reuse_gap names it
        verify(Path(p["path"]), p["sha256"], f"plugin {p.get('name')}")
    for o in (c.get("configuration") or {}).get("overlays", []) or []:
        if not o.get("path") or not is_sha256(o.get("sha256")):
            continue
        verify(Path(o["path"]), o["sha256"], f"overlay {Path(o['path']).name}")
    tool = c.get("tool") or {}
    if tool.get("present") and tool.get("exe"):
        folder = Path(tool["exe"]).parent
        name = Path(tool["exe"]).stem
        for fname, recorded, label in ((f"{name}.dll", tool.get("dllSha256"),
                                        f"the managed assembly {name}.dll"),
                                       ("housecarl-core.dll", tool.get("coreDllSha256"),
                                        "the core assembly housecarl-core.dll")):
            if is_sha256(recorded):
                verify(folder / fname, recorded, label)
    if c.get("baseEmitPath") and c.get("baseEmitSha256"):
        verify(Path(run) / c["baseEmitPath"], c["baseEmitSha256"],
               f"the base emit {c['baseEmitPath']}")
    for row in c.get("inputs") or []:
        if row.get("path") and is_sha256(row.get("sha256")):
            verify(Path(run) / row["path"], row["sha256"],
                   f"the {row.get('part') or 'comparison'} input {row['path']}")
    return "; ".join(changes) or None


def cmd_resume(a) -> int:
    return cmd_run(a, resuming=True)


# -------------------------------------------------------- imported MCP evidence (generic lane) --

IMPORT_DETECTORS = ("finding", "no-finding")
IMPORT_ORIGINS = ("fresh", "reused")


def capture_token(plan_id: str, arm_id: str) -> str:
    """What a capture made for this plan's arm puts in every MCP `initialize` request's `clientInfo.name`."""
    return f"evalrun:{plan_id}:{arm_id}"


def initialize_requests(data: bytes) -> tuple[list[dict] | None, str | None]:
    """The MCP `initialize` requests in a raw request file, or (None, why the file cannot be read as requests).

    The file is JSON Lines, as the stdio capture wrapper writes it: every non-blank line is one JSON value. A value
    that is an array is a JSON-RPC batch whose object members are messages; a whole file that is one JSON document is
    the one-line case. An `initialize` request is an object whose `method` is exactly "initialize". Nothing else is
    read for the token: a token in a call's arguments or an annotation line says nothing about who connected."""
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError as ex:
        return None, f"the request file is not UTF-8 ({ex.reason} at byte {ex.start})"
    found: list[dict] = []
    # Split on "\n" only, as the contract defines the format: `str.splitlines()` also breaks on U+2028, U+2029,
    # U+0085 and the C0 separators, which are legal INSIDE a JSON string, and refusing a capture that carries one in
    # a note or a name would blame the caller for a reader's bug (review R1a finding 2 / R1b F3).
    for n, line in enumerate(text.split("\n"), start=1):
        line = line.rstrip("\r")
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except ValueError:
            return None, f"line {n} of the request file is not JSON, so its requests cannot be read"
        for msg in (value if isinstance(value, list) else [value]):
            if isinstance(msg, dict) and msg.get("method") == "initialize":
                found.append(msg)
    return found, None


def client_name(msg: dict):
    """`params.clientInfo.name` of one initialize request, or None when any level is absent or not an object."""
    params = msg.get("params")
    info = params.get("clientInfo") if isinstance(params, dict) else None
    return info.get("name") if isinstance(info, dict) else None


def origin_refusals(doc: dict, plan: dict, arm_id, captured_time, request: tuple[Path, str] | None
                    ) -> tuple[list[str], dict]:
    """Option A's origin checks for one import document: (refusal sentences, the receipt's origin fields).

    `fresh` must have been captured for THIS plan's arm: every initialize request in the hash-checked request file
    names capture_token(planId, armId) in clientInfo.name, and capturedUtc is not before the plan's createdUtc as
    an instant. `reused` names the evaluation it was captured for; that declaration is recorded, not verified.
    Neither proves authenticity: the token is public and a caller can rewrite every file and hash. These checks
    catch evidence relabelled by mistake."""
    errors: list[str] = []
    origin = doc.get("origin")
    token = capture_token(plan["planId"], str(arm_id))
    how = (f"declare origin fresh for evidence captured for this plan's arm (every MCP initialize request carries "
           f"clientInfo.name \"{token}\" and capturedUtc is not before the plan), or origin reused with reusedFrom "
           "{planId, armId, resultSha256} naming the evaluation it was captured for")
    if "origin" not in doc:
        return [f"origin-missing: the import does not say where its evidence came from; {how}. An envelope written "
                "before origin was required takes one of those two paths"], {}
    if origin not in IMPORT_ORIGINS:
        return [f"origin-invalid: origin must be fresh or reused, found {origin!r}; {how}"], {}
    if origin == "reused":
        src = doc.get("reusedFrom")
        if not isinstance(src, dict):
            return [f"reusedFrom-missing: origin reused must name its source as reusedFrom {{planId, armId, "
                    f"resultSha256}}, found {src!r}"], {}
        bad = [f"reusedFrom.{k} must be a non-empty string" for k in ("planId", "armId")
               if not isinstance(src.get(k), str) or not src.get(k).strip()]
        if not is_sha256(src.get("resultSha256")):
            bad.append("reusedFrom.resultSha256 must be 64-hex: the sha256 of the source run's "
                       "imports/<checkId>__<armId>/result.json, the import document that evaluation accepted")
        if bad:
            return ["reusedFrom-invalid: " + "; ".join(bad)], {}
        if src["planId"] == plan["planId"] and src["armId"] == arm_id:
            return [f"reusedFrom-invalid: reusedFrom names this plan's own arm {arm_id}; reuse means evidence "
                    "captured for another setup or another evaluation, not for this one"], {}
        # Reuse between two setups of THIS plan is legitimate — two setups with identical configurations are measured
        # once — but it is a different statement from reuse across evaluations, and every reader says which
        # (decided 2026-09-17: allow it and word it accurately, rather than forcing a capture per arm).
        within = "this-evaluation" if src["planId"] == plan["planId"] else "another-evaluation"
        if within == "this-evaluation":
            # A within-evaluation source is the ONE source this controller can check without reading another run:
            # both frozen requests are in this plan. The reader is told, as fact, that the evidence came from
            # another setup of this evaluation "whose tested configuration is the same" — so a declared armId that
            # is not an arm of this plan, or is an arm whose frozen request differs, makes that sentence false and
            # is refused here. Reading the SOURCE RUN stays out of scope; this reads only the plan already loaded.
            named = next((a for a in plan.get("arms") or [] if a.get("armId") == src["armId"]), None)
            if named is None:
                return [f"reusedFrom-invalid: reusedFrom names setup {src['armId']!r} of this plan, which has no "
                        f"such arm; a within-evaluation source must be one of this plan's setups "
                        f"({', '.join(str(a.get('armId')) for a in plan.get('arms') or []) or 'none'})"], {}
            here = next((a for a in plan.get("arms") or [] if a.get("armId") == arm_id), None)

            def frozen(a):
                r = (a or {}).get("requested") or {}
                return ([(str(p.get("name") or "").lower(), str(p.get("sha256") or "").lower())
                         for p in r.get("plugins") or []],
                        [(str(v.get("kind") or ""), str(v.get("name") or "").lower())
                         for v in r.get("providers") or []],
                        sorted((str(o.get("kind") or ""), str(o.get("sha256") or "").lower())
                               for o in r.get("overlays") or []),
                        r.get("population"))
            if here is not None and frozen(named) != frozen(here):
                return [f"reusedFrom-invalid: reusedFrom names setup {src['armId']!r} of this plan, whose FROZEN "
                        f"request is not this arm's: a reader of a within-evaluation reuse is told the source "
                        f"setup's tested configuration is the same, and here the plan itself says it is not. "
                        f"Import this evidence against the setup it was measured for, or as origin reused from "
                        f"the evaluation it really came from"], {}
        return [], {"origin": "reused", "reusedWithin": within,
                    "reusedFrom": {"planId": src["planId"], "armId": src["armId"],
                                   "resultSha256": str(src["resultSha256"]).lower()},
                    "reusedFromVerified": False,
                    "reusedFromNote": "declared by the importer's caller: the source run was not read, so "
                                      "resultSha256 is a statement, not verified provenance"}
    # fresh
    if "reusedFrom" in doc:
        errors.append("origin-invalid: origin fresh must not declare reusedFrom; evidence captured for another "
                      "evaluation is imported as origin reused")
    count = 0
    if request is None:
        errors.append(f"origin-unverified: origin fresh requires the request output to be readable so every MCP "
                      f"initialize request can be checked for clientInfo.name \"{token}\"")
    else:
        path, want_sha = request
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != want_sha:
            errors.append(f"output-stale: {path} changed while its requests were being read")
        else:
            inits, why = initialize_requests(data)
            if inits is None:
                errors.append(f"origin-unverified: {why}; origin fresh requires every MCP initialize request to "
                              f"carry clientInfo.name \"{token}\"")
            elif not inits:
                errors.append(f"origin-unverified: the request file contains no MCP initialize request, so it "
                              f"cannot show a capture for this plan's arm; origin fresh requires every initialize "
                              f"request to carry clientInfo.name \"{token}\"")
            else:
                count = len(inits)
                wrong = [n for n in (client_name(m) for m in inits) if n != token]
                if wrong:
                    errors.append(f"origin-mismatch: {len(wrong)} of {len(inits)} MCP initialize request(s) do not "
                                  f"carry clientInfo.name \"{token}\" (found "
                                  + ", ".join(sorted({repr(n) for n in wrong})[:4])
                                  + "), so this evidence was not captured for this plan's arm; if it was captured "
                                    "for another evaluation, import it as origin reused with reusedFrom")
    if captured_time is not None:
        try:
            created = datetime.fromisoformat(str(plan.get("createdUtc")).replace("Z", "+00:00"))
            if created.tzinfo is None:
                raise ValueError("timezone missing")
        except ValueError:
            errors.append(f"origin-unverified: the plan's createdUtc {plan.get('createdUtc')!r} is not a "
                          "timezone-aware timestamp, so a fresh capture time cannot be compared with it")
        else:
            if captured_time < created:
                errors.append(f"origin-mismatch: capturedUtc {doc.get('capturedUtc')} is before this plan was "
                              f"created ({plan.get('createdUtc')}), so it was not captured for this plan")
    if errors:
        return errors, {}
    return [], {"origin": "fresh",
                "originCheck": {"token": token, "initializeRequests": count,
                                "capturedUtc": doc.get("capturedUtc"), "planCreatedUtc": plan.get("createdUtc")}}


def _cmd_import_results(a, runlog: Run) -> int:
    """Bind already-produced MCP evidence to one frozen plan arm and write a normal receipt."""
    run, src = Path(a.run), Path(a.file)
    set_run_store(run)
    plan, why = load_plan(run)
    if plan is None:
        return refuse(why)
    why = request_refusal(plan)
    if why:
        return refuse(why)
    ledger = Ledger(run, plan)
    if ledger.refusal():
        return refuse(ledger.refusal())
    try:
        manifest_bytes = src.read_bytes()
        doc = json.loads(manifest_bytes)
    except (OSError, ValueError) as ex:
        return refuse(f"--file {src}: not readable as JSON ({type(ex).__name__}: {ex})")
    if not isinstance(doc, dict):
        return refuse(f"--file {src}: not readable as a JSON object")
    manifest_sha256 = hashlib.sha256(manifest_bytes).hexdigest()
    if doc.get("schemaVersion") != 1:
        return refuse(f"schema-unsupported: imported result schemaVersion must be 1, found "
                      f"{doc.get('schemaVersion')!r}")
    if doc.get("planId") != plan["planId"]:
        return refuse(f"foreign-run: imported result belongs to plan {doc.get('planId')!r}, this run is "
                      f"{plan['planId']}")
    cid, aid = doc.get("checkId"), doc.get("armId")
    check = next((c for c in plan.get("checks", []) if c.get("checkId") == cid), None)
    arm = next((x for x in plan.get("arms", []) if x.get("armId") == aid), None)
    if check is None:
        return refuse(f"wrong-check: plan {plan['planId']} has no check {cid!r}")
    if (check.get("adapter") or {}).get("kind") == "live-import":
        # A live game driver's receipt: its own validator (evallive.py), which re-reads every launch.
        import evallive
        return evallive.import_live(sys.modules[__name__], a=a, runlog=runlog, run=run, src=src, plan=plan,
                                    ledger=ledger, check=check, arm=arm, doc=doc, manifest_bytes=manifest_bytes)
    if (check.get("adapter") or {}).get("kind") != "mcp-only":
        return refuse(f"wrong-check: {cid} is {(check.get('adapter') or {}).get('kind')!r}, not mcp-only; "
                      "import-results cannot replace a runnable or unsupported detector")
    if arm is None or aid not in [x.get("armId") for x in check.get("arms", [])]:
        return refuse(f"identity-mismatch: check {cid} has no planned arm {aid!r}")
    # The row the plan left OPEN for an import is the one it recorded `mcp-only`. A check the registry answered
    # `not-applicable` on this arm is answered; an import must not turn it into a completed one (review R1b F1).
    row_status = next((x.get("status") for x in check.get("arms") or [] if x.get("armId") == aid), None)
    if row_status != "mcp-only":
        return refuse(f"wrong-check: the plan records {cid} as {row_status!r} on arm {aid}, not mcp-only, so it is "
                      f"not waiting for imported evidence"
                      + (f": {check.get('applicabilityEvidence')}" if str(row_status).startswith("not-applicable")
                         and check.get("applicabilityEvidence") else "")
                      + ". An import completes a check the plan left open; it never overrides the plan's answer")
    key = Ledger.key(cid, aid, None)
    if (ledger.entry(cid, aid, None) or {}).get("status") in TERMINAL_OK:
        return refuse(f"duplicate-receipt: {key} already has accepted evidence")

    captured, tool = doc.get("capturedUtc"), doc.get("tool")
    try:
        identity = Validator.emit_identity({"configuration": doc.get("configuration")})
    except (AttributeError, TypeError):
        identity = None
    result, rows, errors = doc.get("result"), doc.get("outputs"), []
    captured_time = None
    if not captured or not isinstance(tool, dict) or not tool.get("name"):
        errors.append("the import must name capturedUtc and tool.name")
    elif isinstance(captured, str):
        try:
            parsed = datetime.fromisoformat(captured.replace("Z", "+00:00"))
            if parsed.tzinfo is None:
                raise ValueError("timezone missing")
            captured_time = parsed
        except ValueError:
            errors.append("capturedUtc must be an ISO-8601 timestamp with a timezone")
    else:
        errors.append("capturedUtc must be an ISO-8601 timestamp with a timezone")
    if identity is None:
        errors.append("identity-missing: configuration is absent")
    if not isinstance(result, dict):
        errors.append("result is absent")
        result = {}
    detector, findings, coverage = result.get("detector"), result.get("findings"), result.get("coverage")
    if detector not in IMPORT_DETECTORS:
        errors.append(f"result.detector must be one of {', '.join(IMPORT_DETECTORS)}")
    if not isinstance(findings, int) or isinstance(findings, bool) or findings < 0:
        errors.append("result.findings must be a non-negative integer")
    elif (detector == "finding") != (findings > 0):
        errors.append("result.detector contradicts result.findings")
    if not isinstance(coverage, list) or not coverage:
        errors.append("coverage-missing: result.coverage must contain at least one population")
    else:
        judged = unjudgeable = 0
        for i, line in enumerate(coverage):
            if not isinstance(line, dict):
                errors.append(f"coverage-invalid: result.coverage[{i}] must be an object")
                continue
            population = line.get("Population", line.get("population"))
            if not isinstance(population, str) or not population.strip():
                errors.append(f"coverage-invalid: result.coverage[{i}].Population must be a non-empty string")
            count = line.get("Judged", line.get("judged"))
            reasons = line.get("Unjudgeable", line.get("unjudgeable", {}))
            if not isinstance(count, int) or isinstance(count, bool) or count < 0:
                errors.append(f"coverage-invalid: result.coverage[{i}].Judged must be a non-negative integer")
            else:
                judged += count
            if not isinstance(reasons, dict) or any(not isinstance(v, int) or isinstance(v, bool) or v < 0
                                                    for v in reasons.values()):
                errors.append(f"coverage-invalid: result.coverage[{i}].Unjudgeable values must be "
                              "non-negative integers")
            else:
                unjudgeable += sum(reasons.values())
        if not errors and judged <= 0:
            errors.append(f"extraction-failed: imported evidence judged {judged} with "
                          f"{unjudgeable} unjudgeable")
    if not isinstance(rows, list) or len(rows) != 2:
        errors.append("output-missing: outputs must include the raw MCP request and response files")
    elif {str(x.get("role")) for x in rows if isinstance(x, dict)} != {"request", "response"}:
        errors.append("output-missing: outputs must contain exactly the roles request and response")

    sources, request = [], None
    if isinstance(rows, list):
        for i, row in enumerate(rows):
            if not isinstance(row, dict) or not row.get("path") or not is_sha256(row.get("sha256")):
                errors.append(f"output-missing: outputs[{i}] must name path and 64-hex sha256")
                continue
            if not isinstance(row["path"], str) or not row["path"].strip():
                errors.append(f"output-missing: outputs[{i}].path must be a non-empty string")
                continue
            path = Path(row["path"])
            if not path.is_absolute():
                path = src.parent / path
            if not path.is_file():
                errors.append(f"output-missing: {path}")
            elif sha256_file(path) != str(row["sha256"]).lower():
                errors.append(f"output-stale: {path} does not hash to the imported sha256")
            elif path.suffix.lower() in BINARY_SUFFIXES:
                errors.append(f"output-missing: raw evidence {path.name} is a binary excluded from bundles")
            else:
                sources.append(path)
                if row.get("role") == "request":
                    request = (path, str(row["sha256"]).lower())

    # Option A: where the evidence came from, checked on the hash-checked request bytes themselves.
    # This runs BEFORE the identity checks so they know which of two things they are reading: a capture
    # made for this arm, or a declared reuse of a capture made for another setup or evaluation. Only a
    # reuse this function has already accepted — a well-formed `reusedFrom`, not this arm's own, not a
    # bare `origin: reused` — reaches the relaxed filename rule (`_identity_checks` (f)).
    origin_errors, origin_fields = origin_refusals(doc, plan, aid, captured_time, request)
    errors.extend(origin_errors)
    declared_reuse = origin_fields if origin_fields.get("origin") == "reused" else None

    limits = []
    if identity is not None:
        if doc.get("state") != identity.get("state"):
            errors.append(f"population-mismatch: import state {doc.get('state')!r} differs from "
                          f"configuration state {identity.get('state')!r}")
        for kind, values in (("plugin", identity.get("plugins") or []),
                             ("overlay", identity.get("overlays") or [])):
            for row in values:
                path, want = Path(str(row.get("path") or "")), row.get("sha256")
                if not row.get("path") or not path.is_file() or not is_sha256(want):
                    errors.append(f"identity-missing: {kind} {row.get('name') or row.get('kind')} "
                                  "must name an existing path and 64-hex sha256")
                elif sha256_file(path) != str(want).lower():
                    errors.append(f"identity-mismatch: {kind} {row.get('name') or row.get('kind')} "
                                  "does not hash to its stated sha256")
        validator = Validator(run, plan)
        errors.extend(f"{x['cause']}: {x['detail']}" for x in
                      validator._identity_checks(identity, arm, doc, check.get("adapter") or {},
                                                 None, limits, declared_reuse))
        req = arm.get("requested") or {}
        expected_fp = configuration_fingerprint(
            req.get("plugins") or [], req.get("providers") or [], req.get("overlays") or [],
            req.get("population"), identity.get("state"), schema=identity.get("schemaVersion"))
        if identity.get("fingerprint") != expected_fp:
            errors.append(f"identity-mismatch: imported fingerprint "
                          f"{str(identity.get('fingerprint'))[:12]} does not equal this arm's frozen "
                          f"request {expected_fp[:12]}")

    # The controller's own prerequisite gate (`prereq_satisfied`, the one sweep and derived checks use): evidence
    # for a check that could not have run on this arm is not credited here either, whatever its origin.
    gate = SimpleNamespace(ledger=ledger)
    pre = prereq_satisfied(gate, check, aid)
    if pre:
        errors.append(f"blocked-prerequisite: {prereq_block_detail(gate, pre, aid)}. An import is credited only "
                      f"where the check could run; settle {pre} on this arm (`run` or `resume --run RUN` measures "
                      "it; an arm whose order is refused must be fixed and planned again), then import again")
    if errors:
        return refuse("import-results refused: " + "; ".join(errors))

    # A folder left by an import that is no longer credited (its entry was blocked or invalidated later) backs no
    # current receipt; it is kept as it is, and a re-import is copied beside it.
    dest, n = run / "imports" / key, 1
    while dest.exists():
        n += 1
        dest = run / "imports" / f"{key}~{n}"
    dest.parent.mkdir(parents=True, exist_ok=True)
    temp = Path(tempfile.mkdtemp(prefix=key + "-", dir=dest.parent))
    try:
        (temp / "result.json").write_bytes(manifest_bytes)
        for i, path in enumerate(sources, start=1):
            shutil.copy2(path, temp / f"{i:02d}-{path.name}")
        copied_hashes = [sha256_file(temp / f"{i:02d}-{path.name}")
                         for i, path in enumerate(sources, start=1)]
        approved_hashes = [str(row["sha256"]).lower() for row in rows]
        if sha256_file(temp / "result.json") != manifest_sha256 or copied_hashes != approved_hashes:
            return refuse("output-stale: imported evidence changed while it was being copied")
        os.replace(temp, dest)
    finally:
        if temp.exists():
            shutil.rmtree(temp, ignore_errors=True)
    copied = [dest / "result.json"] + [dest / f"{i:02d}-{path.name}"
                                        for i, path in enumerate(sources, start=1)]
    manifest_copy = copied[0]

    runlog.feature("outputs", len(copied), "files")
    receipt = write_receipt(
        run, plan=plan, check=check, arm=arm, part=None,
        adapter={"kind": "mcp-import", "sourceKind": "mcp-only"}, argv=None,
        run_id=runlog.run_id, parent_run_id=None, started=captured, ended=utcnow(), exit_code=0,
        outputs=copied, log=None,
        consumed={"candidateSha256": arm.get("candidateSha256"),
                  "candidatePath": arm.get("candidatePath"),
                  "configuration": identity, "configurationSource": "emit",
                  "registrySha256": plan["registry"]["sha256"], "tool": tool,
                  "optionsSha256": sha256_text(canonical(plan["options"])),
                  "scopeSha256": plan["scope"]["sha256"]},
        validation={"accepted": True, "rejections": [], "identityLimits": limits},
        result={"execution": "completed", "detector": detector, "findings": findings,
                "coverage": coverage, "strength": (check.get("evidence") or {}).get("strength"),
                "verdict": result.get("verdict"), "note": result.get("note")},
        extra={"imported": {"sourceSha256": sha256_file(manifest_copy),
                            "capturedUtc": captured, "tool": tool, **origin_fields}})
    ledger.transition(cid, aid, None, "completed", receipt_id=receipt["receiptId"],
                      detector=detector, extra={"imported": True, "importOrigin": origin_fields["origin"]})
    runlog.artifact(str(manifest_copy))
    runlog.advance()
    print(f"imported {cid} [{aid}]: {detector}, {findings} finding(s), "
          f"{len(copied)} evidence file(s), origin {origin_fields['origin']}"
          + (f" (declared source: plan {origin_fields['reusedFrom']['planId']} arm "
             f"{origin_fields['reusedFrom']['armId']}, not verified)" if origin_fields["origin"] == "reused" else ""))
    return EXIT_OK


def cmd_import_results(a) -> int:
    """Runlogged entry point for the generic MCP evidence importer."""
    set_run_store(Path(a.run))
    with Run("evalrun-import-results", stage_source="counted") as runlog:
        runlog.stage("validate-and-import", total=1, unit="results")
        rc = _cmd_import_results(a, runlog)
        if rc == EXIT_REFUSED_INPUT:
            runlog.refuse("import validation refused; the diagnostic above names the cause",
                          EXIT_REFUSED_INPUT)
        elif rc != EXIT_OK:
            runlog.fail("import failed", rc)
        return rc


# ------------------------------------------------------------------------------ finalize (D7) --

def runtime_observations_sentence(plan: dict, receipts: dict) -> str:
    """What this run says about the engine at play time: nothing, unless an accepted live receipt is bound to it."""
    live = sorted({r.get("checkId") for r in receipts.values()
                   if (r.get("adapter") or {}).get("kind") == "live-import"
                   and (r.get("validation") or {}).get("accepted")})
    if not live:
        return ("no runtime observation was supplied to this run: nothing here says what the engine did at play "
                "time")
    return ("accepted live receipts for " + ", ".join(live) + " say what the engine did in the conditions their "
            "flip plans list, and nothing about any other condition")


def cmd_finalize(a) -> int:
    run = Path(a.run)
    set_run_store(run)
    plan, why = load_plan(run)
    if plan is None:
        print(f"REFUSED: {why}", file=sys.stderr)
        write_refusal(run, refusal_cause(why), why)
        return EXIT_REFUSED_RESULT
    ledger = Ledger(run, plan)
    bad = ledger.refusal()
    if bad:
        print(f"REFUSED: {bad}", file=sys.stderr)
        write_refusal(run, "foreign-run" if "foreign-run" in bad else "ledger-unreadable", bad)
        return EXIT_REFUSED_RESULT

    # D5: the registry and the scope are premises of the frozen plan. Finalize refuses a changed or
    # missing one as a refused RESULT — exit 3 with a refusal disposition written.
    bad = premise_refusal(plan)
    if bad:
        print(f"REFUSED: {bad}", file=sys.stderr)
        write_refusal(run, refusal_cause(bad), bad)
        return EXIT_REFUSED_RESULT

    receipts = read_receipts(run)
    # D7, walked over the LEDGER, never over the receipts directory: every entry evalrun settled -
    # completed, reused, rejected or accounted - wrote a receipt, so a ledger entry whose receipt
    # file is gone is an unexplained gap in the evidence, whatever its status.
    for k, e in ledger.doc["entries"].items():
        if e.get("status") in ("running", "interrupted", "pending", None):
            continue
        if not (Path(run) / "receipts" / f"{k}.json").exists():
            msg = (f"receipt-missing: the ledger records {k} as {e['status']} but "
                   f"receipts/{k}.json is gone")
            print(f"REFUSED: {msg}", file=sys.stderr)
            write_refusal(run, "receipt-missing", msg)
            return EXIT_REFUSED_RESULT
    for k, r in receipts.items():
        if r.get("planId") != plan["planId"]:
            msg = (f"foreign-run: receipts/{k}.json carries planId {r.get('planId')}, this run is "
                   f"{plan['planId']}")
            print(f"REFUSED: {msg}", file=sys.stderr)
            write_refusal(run, "foreign-run", msg)
            return EXIT_REFUSED_RESULT

    with Run("evalrun-finalize", stage_source="counted") as r:
        r.stage("finalize", total=1, unit="dispositions")
        integrity, bad_keys = evidence_integrity(run, plan, ledger, receipts)
        disp = build_disposition(run, plan, ledger, receipts, integrity=integrity, bad_keys=bad_keys)
        write_json(run / "disposition.json", disp)
        (run / "STATUS.md").write_text(status_md(plan, disp), encoding="utf-8")
        r.advance()
    print(f"evaluation.complete: {disp['evaluation']['complete']}   claim: {disp['evaluation']['claim']}")
    for d in integrity["defects"][:20]:
        print(f"   evidence {d['kind']}: {d['key']} {d['path'] or ''}", file=sys.stderr)
    for m in disp["evaluation"]["missing"][:20]:
        print(f"   missing {m['checkId']} [{m['armId']}] {m['status']}")
    if not integrity["ok"]:
        print(f"REFUSED: evidence-integrity: {len(integrity['defects'])} completed entr"
              f"{'y' if len(integrity['defects']) == 1 else 'ies'} no longer match the evidence they were "
              f"derived from. The disposition and STATUS.md are written with the defects listed first; no "
              f"completed claim rests on them. Next action: `resume --run RUN` re-runs the affected "
              f"entries (finalize never rewrites the ledger).", file=sys.stderr)
        return EXIT_REFUSED_RESULT
    return EXIT_OK


def evidence_integrity(run: Path, plan: dict, ledger: Ledger, receipts: dict) -> tuple[dict, dict]:
    """D6: which terminal-ok entries still have the evidence their claim rests on.

    Returns (`{ok, defects}`, `{ledger key -> the status word for evaluation.missing}`). A completed
    ledger entry is a claim about a file on disk; when that file is gone, changed, or belongs to a
    different receipt, the claim is not completed — it is a named defect, and its detector is counted
    nowhere. The walk is the same one `resume` uses (`dependency_keys`), including the run-internal
    inputs a receipt was compared against, so finalize and resume cannot disagree about what a claim
    rests on. Finalize never rewrites the ledger: recovery is `resume`, which re-runs what it must."""
    defects: list[dict] = []
    bad: dict[str, str] = {}
    memo = HashMemo()
    for k, e in ledger.doc["entries"].items():
        if e.get("status") not in TERMINAL_OK:
            continue
        rpath = Path(run) / "receipts" / f"{k}.json"
        r = receipts.get(k)
        if r is None:
            defects.append({"key": k, "kind": "receipt-mismatch", "path": f"receipts/{k}.json",
                            "recorded": e.get("receiptId"), "found": None,
                            "nextAction": "resume: the receipt this claim rests on cannot be read"})
            bad[k] = "receipt-mismatch"
            continue
        if e.get("receiptId") and r.get("receiptId") != e.get("receiptId"):
            defects.append({"key": k, "kind": "receipt-mismatch", "path": rel_to(run, rpath),
                            "recorded": e.get("receiptId"), "found": r.get("receiptId"),
                            "nextAction": "resume: the ledger and the receipt name different measurements"})
            bad[k] = "receipt-mismatch"
            continue
        if not (r.get("validation") or {}).get("accepted"):
            defects.append({"key": k, "kind": "receipt-mismatch", "path": rel_to(run, rpath),
                            "recorded": "accepted", "found": "not accepted",
                            "nextAction": "resume: a terminal-ok entry whose receipt was not accepted "
                                          "cannot complete a check"})
            bad[k] = "receipt-mismatch"
            continue
        # Its own outputs AND the run-internal inputs it was measured against: a diff whose arm emit
        # is gone is not a diff of anything, whatever its own log still says.
        rows = [(o.get("path"), o.get("sha256"), "output") for o in (r.get("outputs") or [])]
        c = r.get("consumed") or {}
        if c.get("baseEmitPath"):
            rows.append((c["baseEmitPath"], c.get("baseEmitSha256"), "base emit"))
        for row in c.get("inputs") or []:
            rows.append((row.get("path"), row.get("sha256"), f"{row.get('part') or 'comparison'} input"))
        for rel, recorded, label in rows:
            if not rel:
                continue
            p = Path(run) / rel
            if recorded is None or not p.exists():
                defects.append({"key": k, "kind": "evidence-missing", "path": rel,
                                "recorded": recorded, "found": None,
                                "nextAction": f"restore the {label} and finalize again, or resume to "
                                              f"re-run the check"})
                bad[k] = f"evidence-missing({rel})"
                break
            got = memo.sha256(p)
            if got != recorded:
                defects.append({"key": k, "kind": "evidence-changed", "path": rel,
                                "recorded": recorded, "found": got,
                                "nextAction": f"resume: the {label} this claim rests on is not the file "
                                              f"that was measured"})
                bad[k] = f"evidence-changed({rel})"
                break

    # Transitively, along the same edges resume uses: an entry whose input's claim is void has no
    # claim of its own left, even when its own bytes are untouched.
    spreading = True
    while spreading:
        spreading = False
        for k, e in ledger.doc["entries"].items():
            if k in bad or e.get("status") not in TERMINAL_OK:
                continue
            for d in dependency_keys(plan, receipts, e):
                if d in bad:
                    bad[k] = f"evidence-dependency({d})"
                    defects.append({"key": k, "kind": "evidence-dependency", "path": None,
                                    "recorded": d, "found": None,
                                    "nextAction": f"resume: this entry rests on {d}, whose evidence is "
                                                  f"not what was measured"})
                    spreading = True
                    break
    return {"ok": not defects, "defects": defects}, bad


def derived_from(root: Path, receipts: dict | None = None) -> dict:
    """What a disposition was derived FROM, so a later verb can tell whether it still holds: the
    ledger's own hash, one hash over the sorted (ledger key, receipt file hash) pairs, and the journal's
    line count."""
    root = Path(root)
    if receipts is None:
        receipts = read_receipts(root)
    pairs = sorted((k, sha256_file(root / "receipts" / f"{k}.json")) for k in receipts)
    journal = root / "journal.jsonl"
    lines = 0
    if journal.exists():
        lines = sum(1 for ln in journal.read_text(encoding="utf-8", errors="replace").splitlines()
                    if ln.strip())
    return {"ledgerSha256": sha256_file(root / "ledger.json"),
            "receipts": sha256_text(canonical(pairs)), "journalLines": lines}


def write_refusal(run: Path, cause: str, detail: str) -> None:
    write_json(Path(run) / "disposition.json",
               {"schemaVersion": SCHEMA_VERSION, "createdUtc": utcnow(),
                "derivedFrom": derived_from(Path(run)),
                "evaluation": {"complete": False, "claim": "incomplete", "refused": cause,
                               "detail": detail, "missing": [], "unknownApplicability": [],
                               "findings": [], "completedNullChecks": [], "completedCatalogues": [],
                               "integrity": {"ok": False, "defects": []}}})
    (Path(run) / "STATUS.md").write_text(
        f"# Evaluation status — REFUSED\n\nThe finalizer refused to state a disposition.\n\n"
        f"- cause: `{cause}`\n- detail: {detail}\n\n"
        f"`evaluation.complete` is false and no claim is made.\n", encoding="utf-8")


def load_decisions(run: Path, plan: dict) -> list[dict]:
    out = []
    d = Path(run) / "decisions"
    if not d.exists():
        return out
    checks = {c["checkId"]: c for c in plan["checks"]}
    for p in sorted(d.glob("*.json")):
        doc = load_json(p)
        if not isinstance(doc, dict):
            out.append({"file": p.name, "accepted": False, "why": "not readable as JSON"})
            continue
        cid, aid = doc.get("checkId"), doc.get("armId")
        check = checks.get(cid)
        row = {"checkId": cid, "armId": aid, "decidedBy": doc.get("decidedBy"),
               "decidedUtc": doc.get("decidedUtc"), "decision": doc.get("decision"),
               "note": doc.get("note"), "evidence": [], "strength": "attributed-decision",
               "accepted": False, "why": None}
        if check is None:
            row["why"] = f"no registry check with id {cid!r}"
        elif not check.get("manualDecisionAllowed"):
            row["why"] = "the registry check does not allow a manual decision"
        elif doc.get("decision") not in ("completed-with-finding", "completed-no-finding", "not-applicable"):
            row["why"] = f"decision {doc.get('decision')!r} is not one of the three permitted values"
        elif not doc.get("decidedBy"):
            row["why"] = "no decidedBy: an attributed decision needs an attribution"
        else:
            missing = []
            for rel in doc.get("evidence") or []:
                f = Path(run) / rel
                if f.exists():
                    row["evidence"].append({"path": rel, "sha256": sha256_file(f)})
                else:
                    missing.append(rel)
            if missing:
                row["why"] = "evidence path(s) do not exist: " + ", ".join(missing)
            else:
                row["accepted"] = True
        if check is not None and check["adapter"].get("kind") in ("mcp-only", "unsupported"):
            row["satisfiesCompletion"] = False
            row["why"] = (row["why"] or "") + (
                "; recorded, but a decision on an mcp-only/unsupported check never satisfies completion")
        else:
            row["satisfiesCompletion"] = row["accepted"]
        out.append(row)
    return out


def build_disposition(run: Path, plan: dict, ledger: Ledger, receipts: dict, *,
                      integrity: dict | None = None, bad_keys: dict | None = None) -> dict:
    integrity = integrity or {"ok": True, "defects": []}
    bad_keys = bad_keys or {}
    decisions = load_decisions(run, plan)
    dec_by_key = {(d["checkId"], d["armId"]): d for d in decisions if d.get("satisfiesCompletion")}

    missing = []
    findings_rows = []
    null_checks = []
    catalogues = []
    unknown_app = []
    for check in plan["checks"]:
        cid = check["checkId"]
        if check["applicability"] == "unknown" and check.get("status") != NOT_OPTED_IN:
            unknown_app.append({"checkId": cid, "evidence": check["applicabilityEvidence"],
                                "effect": "keeps evaluation.complete false"})
            # A-2: an unknown-applicability check is listed under BOTH unknownApplicability and
            # missing. A check that ran and found nothing, on a target we cannot show it applies to,
            # is not a check that is done.
            for arm_row in check["arms"]:
                missing.append({"checkId": cid, "armId": arm_row["armId"], "part": None,
                                "status": "unknown-applicability", "required": check["required"],
                                "family": check["family"],
                                "cause": "applicability is unknown, and unknown never becomes not-applicable: "
                                         + (check["applicabilityEvidence"] or "")})
        for arm_row in check["arms"]:
            aid = arm_row["armId"]
            parts = [k for k in ledger.doc["entries"]
                     if ledger.doc["entries"][k]["checkId"] == cid and ledger.doc["entries"][k]["armId"] == aid]
            if not parts:
                if check["required"] and check["applicability"] in ("yes", "unknown"):
                    missing.append({"checkId": cid, "armId": aid, "part": None, "status": "pending",
                                    "cause": "the check was planned but no receipt was written"})
                continue
            for k in parts:
                e = ledger.doc["entries"][k]
                status, detector = e.get("status"), e.get("detector")
                r = receipts.get(k) or {}
                if k in bad_keys:
                    # D6: the evidence this entry's claim rests on is gone, changed, or belongs to a
                    # different receipt. It completes nothing and is counted nowhere.
                    missing.append({"checkId": cid, "armId": aid, "part": e.get("part"),
                                    "status": bad_keys[k], "required": check["required"],
                                    "family": check["family"],
                                    "cause": "the evidence this completed entry claimed no longer matches "
                                             "the receipt that recorded it; see evaluation.integrity"})
                    continue
                # A detector row is a claim about a measurement that STANDS: only a terminal-ok entry
                # can carry one into findings, completedNullChecks or completedCatalogues.
                if status not in TERMINAL_OK:
                    detector = None
                if detector == "finding":
                    findings_rows.append({"checkId": cid, "armId": aid, "part": e.get("part"),
                                          "findings": (r.get("result") or {}).get("findings"),
                                          "verdict": (r.get("result") or {}).get("verdict"),
                                          "strength": (r.get("result") or {}).get("strength")})
                elif detector == "no-finding":
                    null_checks.append({"checkId": cid, "armId": aid, "part": e.get("part"),
                                        "coverage": len((r.get("result") or {}).get("coverage") or [])})
                elif detector == "catalogue":
                    # A-6: rows that are evidence, not defects. Never a finding, never a null check.
                    cov = ((r.get("result") or {}).get("coverage") or [{}])[0]
                    catalogues.append({"checkId": cid, "armId": aid,
                                       "count": (r.get("result") or {}).get("findings",
                                                                            cov.get("Judged")),
                                       "unit": cov.get("Population", "rows"),
                                       "kind": ((check["adapter"].get("result") or {}).get("kind"))})
                if not (check["required"] and check["applicability"] in ("yes", "unknown")):
                    continue
                ok = status in TERMINAL_OK and detector in ("finding", "no-finding", "catalogue")
                # An arm-level `not-applicable` is an answer, not an absence: the baseline has no
                # order evaluation of itself, and a check whose registry trigger is false on this
                # target was already excluded above.
                if (status or "").startswith("not-applicable"):
                    ok = True
                if not ok and (cid, aid) in dec_by_key:
                    ok = True
                if not ok:
                    cause = e.get("cause") or (
                        "detector-undetermined" if status in TERMINAL_OK and detector is None
                        else "the check did not produce an accepted detector result")
                    missing.append({"checkId": cid, "armId": aid, "part": e.get("part"),
                                    "status": status, "cause": cause,
                                    "required": True, "family": check["family"]})

    complete = not missing and not unknown_app
    if not complete:
        claim = "incomplete"
    elif findings_rows:
        claim = "findings"
    else:
        claim = "no-findings-in-completed-checks"

    arms = {}
    for arm in plan["arms"]:
        # only accepted receipts: a rejected diff's classification is not this arm's order evaluation
        rows = [r for r in receipts.values() if r.get("armId") == arm["armId"] and r.get("orderEvaluation")
                and (r.get("validation") or {}).get("accepted")]
        per_probe = {}
        regression = False
        word = order_classification(run, arm, rows)
        classification = word["classification"]
        probes = []
        for r in rows:
            oe = r["orderEvaluation"]
            for p in oe.get("probes", []):
                probes.append(p)
                created = oe.get("created") or {}
                counts = (oe.get("diff") or {}).get("counts") or {}
                per_probe[p] = {"verdict": (r.get("result") or {}).get("verdict"),
                                "repaired": counts.get("repaired", 0),
                                "reclassified": counts.get("reclassified", 0),
                                "created": created.get("created", 0),
                                "newDefect": created.get("newDefect", 0),
                                "attributed": created.get("attributed", 0),
                                "unmeasured": max(counts.get("unmeasured", 0), created.get("unmeasured", 0))}
                if created.get("newDefect", 0) > 0:
                    regression = True
        entry = {"kind": arm["kind"], "candidateFile": arm["candidateFile"],
                 "loadedInGame": None, "loadedInGameNote": arm["loadedInGameNote"],
                 "payload": arm.get("payload"),
                 "orderEvaluation": {**word, "probes": sorted(set(probes)),
                                     "regression": regression, "perProbe": per_probe,
                                     "scope": "ordersweep's own classification, verbatim, about the "
                                              "probes named and the population and state they ran in"}}
        if arm["kind"] == "supplied-combination" and regression:
            entry["orderEvaluation"]["note"] = (
                "a supplied combination with a new physical defect on ANY probe is a regression whatever "
                "the repaired counts say: repairs on one probe never net against a defect on another")
        arms[arm["armId"]] = entry

    unassessed = {
        "statesNotExplored": plan["states"]["notExplored"],
        "candidatesHeldBack": plan["budgets"]["candidatesHeldBack"],
        "unjudgeablePopulations": [
            {"checkId": r["checkId"], "armId": r["armId"], "population": c.get("Population", c.get("population")),
             "judged": c.get("Judged", c.get("judged")), "unjudgeable": c.get("Unjudgeable", c.get("unjudgeable"))}
            for r in receipts.values() for c in ((r.get("result") or {}).get("coverage") or [])
            if Validator.unjudgeable(c) > 0],
        "familiesUnsupportedOrManual": sorted({c["family"] for c in plan["checks"]
                                               if c["adapter"].get("kind") in
                                               ("unsupported", "mcp-only", "manual", "media")}),
        "scopeUnmeasured": plan["scope"].get("unmeasured", []),
        "mediaUnassessed": "media is identified and assessed by `bundle --media`; see the bundle manifest",
        "runtimeObservations": runtime_observations_sentence(plan, receipts),
        "notOptedIn": [c["checkId"] for c in plan["checks"] if c.get("status") == NOT_OPTED_IN],
    }

    discovery = {
        "sourcesSearched": sorted({(r.get("adapter") or {}).get("exe")
                                   for r in receipts.values()
                                   if (r.get("adapter") or {}).get("kind") in ("adviceprobe", "patchprobe")
                                   and r["validation"]["accepted"]} - {None}),
        "sourcesNotSearched": ["the Nexus network API (this runner never fetches; advice is read offline "
                               "from the catalogue snapshot)"],
        "candidates": next((r.get("derived", {}).get("candidates", [])
                            for r in receipts.values() if (r.get("derived") or {}).get("candidates")), []),
        "advice": [{"checkId": r["checkId"], "armId": r["armId"],
                    "provenance": (r.get("consumed") or {}).get("configuration", {}).get("source"),
                    "staleness": "read offline from the stored catalogue snapshot; the cached source's age "
                                 "is not the date this report was assembled"}
                   for r in receipts.values() if (r.get("adapter") or {}).get("kind") == "adviceprobe"],
        "listings": [c for c in catalogues if c.get("kind") == "discovery"],
        "note": "a Nexus hit is a lead: a downloadable file stays unqualified until its actual payload was "
                "in an evaluated arm",
    }

    evaluation_refused = None if integrity["ok"] else "evidence-integrity"
    return {
        "schemaVersion": SCHEMA_VERSION, "planId": plan["planId"], "createdUtc": utcnow(),
        "registryVersion": plan["registry"]["registryVersion"],
        "runIds": ledger.doc.get("runIds", []),
        # What this disposition was derived FROM, so `bundle` can refuse a stale one by name rather
        # than carrying yesterday's claims into today's package.
        "derivedFrom": derived_from(run, receipts),
        "evaluation": {"complete": complete, "claim": claim, "missing": missing,
                       "unknownApplicability": unknown_app,
                       "integrity": integrity, "refused": evaluation_refused,
                       "findings": findings_rows, "completedNullChecks": null_checks,
                       "completedCatalogues": catalogues,
                       "claimVocabulary": list(CLAIMS),
                       "note": "the words pass, compatible and clean are not claims this finalizer makes; "
                               "a completed check that found a defect is a completed check with a finding"},
        "arms": arms,
        "manualDecisions": decisions,
        "unassessed": unassessed,
        "explicitDecisions": plan["explicitDecisions"],
        "discovery": discovery,
        "catalogues": catalogues,
        "importedEvidence": imported_evidence(ledger, receipts, bad_keys),
        "ledgerCounts": _counts(ledger),
    }


def imported_evidence(ledger: Ledger, receipts: dict, bad_keys: dict) -> list[dict]:
    """Where each CREDITED import's evidence came from, as its receipt records it. A receipt written before an import
    had to declare origin says nothing, and that is reported as `unknown` - never as fresh. `reused` is the import's
    own declaration about another evaluation, not the controller re-using this run's receipt (ledger `reused`)."""
    rows = []
    for k, e in sorted(ledger.doc["entries"].items()):
        r = receipts.get(k) or {}
        imp = r.get("imported")
        if (e.get("status") not in TERMINAL_OK or k in bad_keys or not isinstance(imp, dict)
                or not (r.get("validation") or {}).get("accepted")):
            continue
        origin = imp.get("origin") if imp.get("origin") in IMPORT_ORIGINS else "unknown"
        row = {"checkId": e["checkId"], "armId": e["armId"], "receiptId": r.get("receiptId"), "origin": origin,
               "capturedUtc": imp.get("capturedUtc")}
        if origin == "reused":
            row["reusedFrom"] = imp.get("reusedFrom")
            row["reusedFromVerified"] = bool(imp.get("reusedFromVerified"))
            # What the capture says about the file it was measured against. THREE states, and the reader is
            # told which: the capture names another file (`other-file`), the capture names no file at all
            # (`unstated` - a real limitation on what can be checked, which used to vanish because every
            # consumer filtered on a truthy name), or the names agree and there is nothing extra to say (no
            # `sourceDisclosure` key - appending "measured against <this setup's own file>" to "not measured
            # for this setup" would read as a contradiction). The state is read from the identity limit rule
            # (f) wrote, never re-derived here: one rule decides it, so the disposition and the receipt cannot
            # disagree (reviews 2026-09-17, and Piranha's handback on the `unstated` case).
            lim = next((x for x in (r.get("validation") or {}).get("identityLimits") or []
                        if isinstance(x, dict) and x.get("armCandidate")), None)
            if lim is not None:
                row["sourceDisclosure"] = "other-file" if lim.get("capturedAgainst") else "unstated"
                row["capturedAgainst"] = lim.get("capturedAgainst") or None
                row["armCandidate"] = lim.get("armCandidate")
            # which statement this is: reuse between two setups of this evaluation, or from another evaluation. A
            # receipt written before the distinction existed says neither, and is reported as `unstated`.
            row["reusedWithin"] = (imp.get("reusedWithin")
                                   if imp.get("reusedWithin") in ("this-evaluation", "another-evaluation")
                                   else "unstated")
        elif origin == "unknown":
            row["note"] = ("imported before an import had to declare its origin: whether this evidence was captured "
                           "for this plan is not recorded")
        rows.append(row)
    return rows


def _counts(ledger: Ledger) -> dict:
    out: dict[str, int] = {}
    for e in ledger.doc["entries"].values():
        base = re.sub(r"\(.*\)$", "(...)", e.get("status") or "?")
        out[base] = out.get(base, 0) + 1
    return dict(sorted(out.items()))


def status_md(plan: dict, disp: dict) -> str:
    ev = disp["evaluation"]
    L = [f"# Evaluation status — plan `{plan['planId']}`", "",
         f"- registry version {plan['registry']['registryVersion']} (`{plan['registry']['sha256'][:12]}`)",
         f"- arms: " + ", ".join(f"`{a['armId']}` ({a['kind']})" for a in plan["arms"]),
         f"- states selected: " + ", ".join(plan["states"]["selected"]),
         f"- **evaluation.complete: {ev['complete']}**   claim: `{ev['claim']}`", "",
         "This is the review index, not the report. Every line below is derived from the ledger and the "
         "validated receipts; nothing here is a judgement this runner made about the mod.", ""]
    integrity = ev.get("integrity") or {"ok": True, "defects": []}
    if not integrity.get("ok"):
        # Defects first: a reader must not scroll past a completed claim to learn its evidence is gone.
        L += ["## Evidence integrity — REFUSED", "",
              f"`evaluation.refused: evidence-integrity`. {len(integrity['defects'])} completed entr"
              f"{'y' if len(integrity['defects']) == 1 else 'ies'} no longer match the evidence recorded "
              f"for them, so nothing below counts them as completed.", "",
              "| entry | defect | path | recorded | found | next action |",
              "|---|---|---|---|---|---|"]
        for d in integrity["defects"]:
            L.append(f"| `{d['key']}` | `{d['kind']}` | {d['path'] or '-'} | "
                     f"{str(d['recorded'] or '-')[:12]} | {str(d['found'] or '-')[:12]} | "
                     f"{d['nextAction']} |")
        L += ["", "The ledger was not rewritten. `resume --run RUN` invalidates and re-runs the affected "
                  "entries.", ""]
    L += ["## Findings", ""]
    if ev["findings"]:
        L += ["| check | arm | part | findings | verdict | strength |", "|---|---|---|---|---|---|"]
        for f in ev["findings"]:
            L.append(f"| `{f['checkId']}` | {f['armId']} | {f['part'] or '-'} | {f['findings']} | "
                     f"{f['verdict'] or '-'} | {f['strength'] or '-'} |")
    else:
        L.append("No completed check reported a finding. That is a statement about the completed checks only.")
    L += ["", "## Completed null checks", ""]
    if ev["completedNullChecks"]:
        for c in ev["completedNullChecks"]:
            L.append(f"- `{c['checkId']}` [{c['armId']}{'/' + c['part'] if c['part'] else ''}] — no finding, "
                     f"{c['coverage']} coverage line(s) declared")
    else:
        L.append("- none")
    L += ["", "## Completed catalogues (rows that are evidence, not defects)", ""]
    if ev["completedCatalogues"]:
        for c in ev["completedCatalogues"]:
            L.append(f"- `{c['checkId']}` [{c['armId']}] — {c['count']} {c['unit']} catalogued "
                     f"({c['kind']}); a catalogue completes its check and is never a finding")
    else:
        L.append("- none")
    imports = disp.get("importedEvidence") or []
    if imports:
        L += ["", "## Imported evidence and where it came from", "",
              "Origin is checked for accidental relabelling only: a capture token and timestamps are public and "
              "rewritable, so this is not proof of authenticity.", "",
              "| check | arm | origin |", "|---|---|---|"]
        for x in imports:
            if x["origin"] == "fresh":
                said = "fresh: captured for this plan's arm (capture token and time checked)"
            elif x["origin"] == "reused":
                src = x.get("reusedFrom") or {}
                where = ("reused from setup `%s` of THIS plan; not measured for this setup" % src.get("armId")
                         if x.get("reusedWithin") == "this-evaluation" else
                         "reused from plan `%s` arm `%s`; not measured for this plan"
                         % (src.get("planId"), src.get("armId")))
                said = (f"{where} (result `{str(src.get('resultSha256'))[:12]}`, declared, not verified)"
                        + ("" if x.get("reusedWithin") != "unstated" else
                           "; the receipt does not say whether the source is this evaluation or another")
                        + ("; the capture states it was measured against "
                           f"`{x.get('capturedAgainst')}`" if x.get("sourceDisclosure") == "other-file"
                           else "; the capture names NO candidate file, so which file it was measured "
                                "against is not stated at all" if x.get("sourceDisclosure") == "unstated"
                           else ""))
            else:
                said = "origin not recorded: imported before origin was required"
            L.append(f"| `{x['checkId']}` | {x['armId']} | {said} |")
    L += ["", "## Missing checks (each keeps the evaluation incomplete)", ""]
    if ev["missing"]:
        L += ["| check | arm | status | cause |", "|---|---|---|---|"]
        for m in ev["missing"]:
            L.append(f"| `{m['checkId']}` | {m['armId']} | `{m['status']}` | {m['cause']} |")
    else:
        L.append("- none")
    if ev["unknownApplicability"]:
        L += ["", "## Unknown applicability (never read as not-applicable)", ""]
        for u in ev["unknownApplicability"]:
            L.append(f"- `{u['checkId']}` — {u['evidence']}")
    L += ["", "## Order evaluation per arm", ""]
    for aid, arm in disp["arms"].items():
        oe = arm["orderEvaluation"]
        L.append(f"- `{aid}` ({arm['kind']}): classification **{oe['classification'] or 'n/a'}**, "
                 f"regression: {oe['regression']}, probes: {', '.join(oe['probes']) or 'none'}")
        if oe.get("classificationNote"):
            L.append(f"  - {oe['classificationNote']}")
        if arm.get("payload", {}).get("espOnly"):
            L.append(f"  - {arm['payload']['note']}")
        if oe.get("note"):
            L.append(f"  - {oe['note']}")
        for p, v in oe["perProbe"].items():
            L.append(f"  - {p}: {v['verdict']} — repaired {v['repaired']}, reclassified {v['reclassified']}, "
                     f"created {v['created']} (new {v['newDefect']} / attributed {v['attributed']}), "
                     f"unmeasured {v['unmeasured']}")
    L += ["", "## Limitations and what was not assessed", ""]
    u = disp["unassessed"]
    for label, key in (("states not explored", "statesNotExplored"),
                       ("candidates held back", "candidatesHeldBack"),
                       ("families unsupported or manual", "familiesUnsupportedOrManual"),
                       ("scope extents the footprint probe does not read", "scopeUnmeasured")):
        L.append(f"- {label}: {canonical(u[key])[:400]}")
    for p in u.get("unjudgeablePopulations") or []:
        # A completed check can still leave part of its population unjudged (a target an arm does not
        # load has no footprint there); it is stated here, not only counted in the null-check list.
        L.append(f"- `{p['checkId']}` [{p['armId']}] {p['population']}: {p['judged']} judged, not judged "
                 f"{canonical(p['unjudgeable'])}")
    L.append(f"- media: {u['mediaUnassessed']}")
    L.append(f"- runtime: {u['runtimeObservations']}")
    L += ["", "## Regeneration needs", ""]
    regen = [c for c in plan["checks"] if (c.get("evidence") or {}).get("regeneration")]
    if regen:
        for c in regen:
            L.append(f"- `{c['checkId']}`: {c['evidence']['regeneration']}")
    else:
        L.append("- none declared by the registry for the checks in this plan")
    L += ["", "## Explicit decisions", ""]
    ed = plan["explicitDecisions"]
    L.append(f"- --select: {ed['select'] if ed['select'] is not None else 'not used'}")
    L.append(f"- --exclude: {ed['exclude'] or 'not used'}")
    if disp["manualDecisions"]:
        L += ["", "## Manual decisions", ""]
        for d in disp["manualDecisions"]:
            L.append(f"- `{d['checkId']}` [{d['armId']}] {d['decision']} by {d['decidedBy']} — "
                     f"accepted: {d['accepted']}"
                     + (f"; {d['why']}" if d.get("why") else ""))
    L += ["", "## Ledger", "", "| status | entries |", "|---|---|"]
    for k, n in disp["ledgerCounts"].items():
        L.append(f"| `{k}` | {n} |")
    return "\n".join(L) + "\n"


# --------------------------------------------------------------------------------- bundle (D8) --

def bundle_refusal(run: Path, plan: dict, ledger: Ledger, receipts: dict, decisions: list) -> str | None:
    """D7: a package is refused, by name, when it would have to carry a claim whose evidence is not
    there. Every cause below names the receipt and the path, because "the bundle is incomplete" is not
    something a reader can act on."""
    run = Path(run)
    # A-9 (V's finding 1): a control file missing from the RUN must be a refusal here, not a path that
    # quietly drops out of `manifest.required` because nothing was copied under it — a bundle that lost
    # STATUS.md verified complete on the first build.
    for name in CONTROL_FILES:
        if name == "disposition.json":
            continue  # its own cause below, with its own next action
        if not (run / name).exists():
            return (f"control-missing: {name} is not in the run directory. Every control file is part of the "
                    f"bundle's required set, and a manifest that omitted one could not prove completeness. "
                    f"Next action: STATUS.md is rewritten by `finalize`; plan.json, plan.sha256, ledger.json "
                    f"and journal.jsonl cannot be regenerated, so without them this run is not packageable.")
    disp = load_json(run / "disposition.json")
    if not isinstance(disp, dict):
        return (f"disposition-missing: {run / 'disposition.json'} is absent or unreadable. A bundle "
                f"carries a disposition's claims; there are none to carry. Next action: finalize first.")
    if disp.get("planId") != plan["planId"]:
        return (f"disposition-foreign: disposition.json carries planId {disp.get('planId')}, this run is "
                f"{plan['planId']}. Next action: finalize this run.")
    if (disp.get("evaluation") or {}).get("refused"):
        return (f"disposition-refused: the disposition records "
                f"`{disp['evaluation']['refused']}` and makes no claim. Next action: fix the cause it "
                f"names, then finalize again.")
    want = derived_from(run, receipts)
    got = disp.get("derivedFrom")
    if got != want:
        return (f"disposition-stale: the disposition was derived from ledger "
                f"{str((got or {}).get('ledgerSha256'))[:12]} / receipts "
                f"{str((got or {}).get('receipts'))[:12]} / journal {(got or {}).get('journalLines')} "
                f"line(s), the run now holds {str(want['ledgerSha256'])[:12]} / "
                f"{str(want['receipts'])[:12]} / journal {want['journalLines']} line(s). Next action: "
                f"finalize again.")
    for k, e in ledger.doc["entries"].items():
        if e.get("status") not in TERMINAL_OK:
            continue
        if not (run / "receipts" / f"{k}.json").exists():
            return (f"receipt-missing: the ledger records {k} as {e['status']} but receipts/{k}.json is "
                    f"gone. Next action: resume --run RUN.")
    # The SAME walk finalize used (D6), so the two verbs cannot disagree about what a claim rests on.
    integrity, _ = evidence_integrity(run, plan, ledger, receipts)
    for d in integrity["defects"]:
        if d["kind"] == "evidence-missing":
            return (f"evidence-missing: the ledger entry {d['key']} claims {d['path']}, which is not on "
                    f"disk. A bundle never drops a required file from its manifest to stay complete. "
                    f"Next action: {d['nextAction']}.")
        if d["kind"] == "evidence-changed":
            return (f"evidence-changed: {d['key']} recorded {d['path']} at {str(d['recorded'])[:12]}; it "
                    f"now hashes {str(d['found'])[:12]}. A freshly computed hash is never adopted for a "
                    f"receipt's output. Next action: {d['nextAction']}.")
        if d["kind"] == "evidence-dependency":
            return (f"evidence-missing: {d['key']} rests on {d['recorded']}, whose evidence is not what "
                    f"was measured. Next action: {d['nextAction']}.")
        return (f"receipt-mismatch: {d['key']} — recorded {d['recorded']}, found {d['found']}. "
                f"Next action: {d['nextAction']}.")
    for d in decisions:
        if not d.get("accepted"):
            continue
        for row in d.get("evidence") or []:
            if not (run / row["path"]).exists():
                return (f"decision-evidence-missing: the accepted decision on {d['checkId']} "
                        f"[{d['armId']}] cites {row['path']}, which is not on disk. Next action: restore "
                        f"it, or withdraw the decision.")
    return None


#: media item schema 2 (standards/HOUSECARL_EVALUATION_BUNDLE.md §8). The vocabularies are closed and spelled
#: here so a typo is a KeyError at the call site rather than an invented reason in a bundle.
MEDIA_KINDS = ("offline-render", "native-screenshot", "game-video", "historical-illustration")
FINGERPRINT_SOURCES = ("emitted", "asserted")
MEDIA_REASONS = ("file-missing", "kind-missing", "subject-missing", "state-missing", "fingerprint-missing",
                 "fingerprint-source-missing", "provenance-missing", "no-accepted-receipt-on-arm",
                 "fingerprint-mismatch", "source-missing", "original-missing")


def media_item_refusal(item: dict, src: Path, fingerprints: dict,
                       other_fingerprints: dict | None = None) -> tuple[str | None, str | None]:
    """Why a media item is unassessed: ONE closed reason from MEDIA_REASONS plus a plain sentence, or
    (None, None) when it is accepted. The checks run in the standard's order — the file, then the
    schema-2 fields (kind, subject, state, fingerprint, fingerprint source, provenance), then the arm's
    accepted receipts, then the fingerprint comparison, then the clip's source and the original. A schema-1 item
    (no `mediaKind`, no `fingerprintSource`) is refused by the first missing field's name.

    `fingerprints` is each arm's OWN configuration (its accepted part-null / part-arm receipts);
    `other_fingerprints` is what the arm's reverse / diff parts consumed — the other order — and only ever
    explains a refusal (A-3)."""
    other_fingerprints = other_fingerprints or {}
    def r(reason: str, detail: str) -> tuple[str, str]:
        assert reason in MEDIA_REASONS, reason
        return reason, detail

    if not item.get("path") or not src.exists() or src.is_dir():
        return r("file-missing", f"the file {item.get('path')!r} does not exist")
    kind = item.get("mediaKind")
    if kind not in MEDIA_KINDS:
        return r("kind-missing", (f"mediaKind is absent (a schema-1 item)" if kind is None else
                                  f"mediaKind {kind!r} is not one of {', '.join(MEDIA_KINDS)}"))
    subject = item.get("subject")
    if not isinstance(subject, dict) or not all(subject.get(k) for k in ("checkId", "armId", "description")):
        return r("subject-missing", "subject must carry checkId, armId and description")
    if not item.get("state"):
        return r("state-missing", "the item names no world state")
    fp = item.get("configurationFingerprint")
    if not fp:
        return r("fingerprint-missing",
                 "the item names no configuration fingerprint: it cannot be tied to a measured arm")
    fps = item.get("fingerprintSource")
    if fps not in FINGERPRINT_SOURCES:
        return r("fingerprint-source-missing",
                 (f"fingerprintSource is absent (a schema-1 item)" if fps is None else
                  f"fingerprintSource {fps!r} is not one of {', '.join(FINGERPRINT_SOURCES)}"))
    prov = item.get("provenance")
    if not isinstance(prov, dict) or not prov.get("tool") or not prov.get("capturedUtc"):
        return r("provenance-missing", "provenance must be an object naming tool and capturedUtc")
    aid = subject["armId"]
    if aid not in fingerprints:
        return r("no-accepted-receipt-on-arm",
                 f"no accepted receipt measured arm {aid!r}'s own configuration"
                 + (" (its reverse / diff parts measured the other order)" if aid in other_fingerprints else ""))
    if fp not in fingerprints[aid]:
        if fp in other_fingerprints.get(aid, set()):
            return r("fingerprint-mismatch",
                     f"the fingerprint {str(fp)[:12]} belongs to the other order this arm's reverse part "
                     f"measured, not to arm {aid}'s own configuration; a picture of the baseline order "
                     f"illustrates the baseline arm")
        return r("fingerprint-mismatch",
                 f"the fingerprint {str(fp)[:12]} does not match any accepted receipt's configuration on "
                 f"arm {aid}: this is not an illustration of a verified result")
    interval = item.get("sourceInterval")
    if interval is not None:
        if not isinstance(interval, dict) or not interval.get("sourcePath"):
            return r("source-missing", "sourceInterval names no sourcePath")
        sp = Path(interval["sourcePath"])
        if not sp.exists() or sp.is_dir():
            return r("source-missing", f"the clip's source {interval['sourcePath']!r} does not exist")
        if not interval.get("sourceSha256"):
            return r("source-missing", "sourceInterval names no sourceSha256 to check the source against")
        got = sha256_file(sp)
        if got != interval["sourceSha256"]:
            return r("source-missing", f"the clip's source hashes to {got[:12]}, not the recorded "
                                       f"{str(interval['sourceSha256'])[:12]}")
    original = item.get("original")
    if original is not None:
        if not isinstance(original, dict) or not original.get("path"):
            return r("original-missing", "original names no path")
        op = Path(original["path"])
        if not op.exists() or op.is_dir():
            return r("original-missing", f"the original {original['path']!r} does not exist")
        if not original.get("sha256"):
            return r("original-missing", "original names no sha256 to check the file against")
        got = sha256_file(op)
        if got != original["sha256"]:
            return r("original-missing", f"the original hashes to {got[:12]}, not the recorded "
                                         f"{str(original['sha256'])[:12]}")
    return None, None


def cmd_bundle(a) -> int:
    run = Path(a.run)
    set_run_store(run)
    plan, why = load_plan(run)
    if plan is None:
        return refuse(why)
    out = Path(a.out) if a.out else run / "bundle"
    ledger = Ledger(run, plan)
    if ledger.refusal():
        return refuse(ledger.refusal())

    receipts = read_receipts(run)
    decisions = load_decisions(run, plan)
    bad = bundle_refusal(run, plan, ledger, receipts, decisions)
    if bad:
        print(f"REFUSED: {bad}", file=sys.stderr)
        return EXIT_REFUSED_RESULT

    with Run("evalrun-bundle", stage_source="counted") as r:
        r.stage("bundle", total=1, unit="bundles")
        out.mkdir(parents=True, exist_ok=True)
        evidence = []
        copied: dict[str, dict] = {}

        def copy_in(src: Path, rel: str, *, check_id=None, arm_id=None, strength=None,
                    kind="artifact", provenance=None, recorded_sha256=None, recorded_by=None) -> None:
            """Copy one file in and index it. D7: when the receipt RECORDED a hash for this file, the
            copy happens only if the file on disk still hashes to it, and the manifest row carries the
            RECORDED hash and the receipt that recorded it — never a freshly computed one that would
            certify whatever happens to be there now."""
            src = Path(src)
            rel = Path(rel).as_posix()
            if rel in copied or not src.exists() or src.is_dir():
                return
            if src.suffix.lower() in BINARY_SUFFIXES:
                return
            got = sha256_file(src)
            if recorded_sha256 is not None and got != recorded_sha256:
                return  # bundle_refusal already refuses this; never copy it under a new hash
            dst = out / rel
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
            row = {"id": sha256_text(rel)[:16], "kind": kind,
                   "path": rel, "sha256": recorded_sha256 if recorded_sha256 is not None else got,
                   "bytes": dst.stat().st_size, "checkId": check_id, "armId": arm_id,
                   "strength": strength, "provenance": provenance, "recordedBy": recorded_by}
            evidence.append(row)
            copied[rel] = row

        required: list[str] = []

        def require(rel: str) -> None:
            """A-9: a required path is required whether or not the copy happened. A path that failed
            to copy must show up as `required-missing` at verify, never vanish from the list."""
            rel = Path(rel).as_posix()
            if rel not in required:
                required.append(rel)

        controls = CONTROL_FILES
        for name in controls:
            copy_in(run / name, name, kind="control")
        for sub in ("receipts", "decisions", "candidates"):
            for p in sorted((run / sub).glob("**/*")):
                if p.is_file():
                    copy_in(p, f"{sub}/{p.relative_to(run / sub).as_posix()}", kind=sub.rstrip("s"))
        for k, rec in receipts.items():
            accepted = bool((rec.get("validation") or {}).get("accepted"))
            for o in rec.get("outputs") or []:
                src = run / o["path"]
                copy_in(src, f"evidence/{o['path']}", check_id=rec["checkId"], arm_id=rec["armId"],
                        strength=(rec.get("result") or {}).get("strength"), kind="emit",
                        provenance={"receiptId": rec["receiptId"], "runId": rec.get("runId")},
                        recorded_sha256=o.get("sha256") if accepted else None,
                        recorded_by=rec["receiptId"] if accepted else None)
            if rec.get("log"):
                # A sweep job log keeps its RUN-relative path: two launches write logs of the same name, and
                # flattening them into logs/ kept the first and attributed it to both.
                log_rel = Path(rec["log"]).as_posix()
                copy_in(run / rec["log"], log_rel if log_rel.startswith("sweep/") else f"logs/{Path(rec['log']).name}",
                        check_id=rec["checkId"], arm_id=rec["armId"], kind="log",
                        provenance={"receiptId": rec["receiptId"]})
        for d in decisions:
            if not d.get("accepted"):
                continue
            for row in d.get("evidence") or []:
                copy_in(run / row["path"], f"evidence/{row['path']}", check_id=d["checkId"],
                        arm_id=d["armId"], kind="decision-evidence", strength="attributed-decision",
                        provenance={"decidedBy": d.get("decidedBy")})
        for p in sorted((run / "sweep").glob("**/*")):
            if p.is_file() and p.suffix.lower() in (".json", ".jsonl", ".md", ".txt"):
                copy_in(p, f"sweep/{p.relative_to(run / 'sweep').as_posix()}", kind="sweep")
        if plan["scope"].get("path") and Path(plan["scope"]["path"]).exists():
            copy_in(Path(plan["scope"]["path"]), "scope/scope.json", kind="scope")
        if Path(plan["registry"]["path"]).exists():
            copy_in(Path(plan["registry"]["path"]), "registry/check-registry.json", kind="registry")
        search = Path(plan["candidatesDir"]) / "candidates.json"
        if search.exists():
            copy_in(search, "search/candidates.json", kind="search")

        unassessed_media = []
        media_doc = load_json(Path(a.media)) if a.media else None
        if a.media and media_doc is None:
            return refuse(f"--media {a.media}: not readable as JSON")
        # A-3: an arm's comparison set is the fingerprint of its OWN configuration — the accepted receipts
        # whose part is null or "arm". A `reverse` part is measured on the OTHER order (the baseline for a
        # candidate arm) and its receipt carries that order's fingerprint; a picture of the baseline order
        # illustrates the baseline arm, never the candidate, so those fingerprints are kept apart and used
        # only to say why an item was refused.
        fingerprints: dict[str, set] = {}
        other_fingerprints: dict[str, set] = {}
        for rec in receipts.values():
            if rec["validation"]["accepted"]:
                fp = (rec.get("consumed") or {}).get("configuration", {}).get("fingerprint")
                if fp:
                    target = fingerprints if rec.get("part") in (None, "arm") else other_fingerprints
                    target.setdefault(rec["armId"], set()).add(fp)
        media_items = media_doc if isinstance(media_doc, list) else (media_doc or {}).get("items") or []
        for item in media_items:
            if not isinstance(item, dict):
                return refuse(f"--media {a.media}: an item is not an object: {str(item)[:80]!r}")
            src = Path(item.get("path") or "")
            if src.suffix.lower() in BINARY_SUFFIXES:
                return refuse(f"--media {a.media}: {src.name} has a suffix that is never copied by policy "
                              f"({src.suffix}); a media item is an image or video file")
            reason, detail = media_item_refusal(item, src, fingerprints, other_fingerprints)
            if reason:
                unassessed_media.append({**item, "reason": reason, "detail": detail})
                continue
            subject = item["subject"]
            digest = sha256_file(src)
            rel = f"media/{digest[:12]}-{src.name}"
            copy_in(src, rel, check_id=subject.get("checkId"), arm_id=subject.get("armId"),
                    strength="supplied-media", kind="media", provenance=item.get("provenance"))
            row = copied.get(rel)
            if row is None:
                return refuse(f"--media {a.media}: {src} could not be copied into the bundle")
            # the manifest row carries EVERY field of the item (schema 2) on top of id/sha256/bytes;
            # `path` becomes the bundle-relative copy, and the row's own keys are never overwritten
            for k, v in item.items():
                if k not in ("path", "sha256", "bytes", "id", "kind", "checkId", "armId", "strength",
                             "recordedBy"):
                    row[k] = v

        # D7: what a reader may DEMAND of this bundle. A manifest that simply lists what it happened to
        # copy cannot prove completeness — an omitted file would make the omission invisible.
        for name in controls:
            require(name)
        require("registry/check-registry.json")
        if plan["scope"].get("path"):
            require("scope/scope.json")
        for k, e in ledger.doc["entries"].items():
            if e.get("status") in TERMINAL_OK:
                require(f"receipts/{k}.json")
        for k, rec in receipts.items():
            if not (rec.get("validation") or {}).get("accepted"):
                continue
            for o in rec.get("outputs") or []:
                if Path(o["path"]).suffix.lower() in BINARY_SUFFIXES:
                    continue  # never copied by policy (identity only), so never a required path
                require(f"evidence/{o['path']}")
        for d in decisions:
            if d.get("accepted"):
                for row in d.get("evidence") or []:
                    require(f"evidence/{row['path']}")

        manifest = {
            "bundleSchema": BUNDLE_SCHEMA, "planId": plan["planId"], "createdUtc": utcnow(),
            "runIds": ledger.doc.get("runIds", []),
            "required": required,
            "evidence": evidence,
            "plan": "plan.json", "ledger": "ledger.json", "journal": "journal.jsonl",
            "disposition": "disposition.json", "status": "STATUS.md",
            "registry": "registry/check-registry.json",
            "search": "search/candidates.json", "scope": "scope/scope.json",
            "discovery": (load_json(run / "disposition.json") or {}).get("discovery", {}),
            "excluded": [{"what": "plugin and asset binaries",
                          "why": "identities only: a shareable report package refers to a plugin by name "
                                 "and content hash and never carries its bytes"}],
            "unassessedMedia": unassessed_media,
            "note": "bundle assembly and evaluation completeness are two different states: this bundle can "
                    "be complete while the evaluation is not. Read disposition.json for the second.",
        }
        write_json(out / "manifest.json", manifest)
        r.advance()
    print(f"bundle: {len(evidence)} evidence file(s) under {out}"
          + (f"; {len(unassessed_media)} media item(s) unassessed" if unassessed_media else ""))
    return EXIT_OK


MANIFEST_KEYS = ("bundleSchema", "planId", "required", "evidence", "plan", "ledger", "journal",
                 "disposition", "status", "registry")

#: The six control files of a run. Every one is required in a bundle (D7), and a run missing one cannot
#: be packaged (`control-missing`); disposition.json has its own cause because its remedy differs.
CONTROL_FILES = ("plan.json", "plan.sha256", "ledger.json", "journal.jsonl", "disposition.json", "STATUS.md")


def cmd_verify_bundle(a) -> int:
    """D8: structural validity, then hashes, then the evidence chain — reading NOTHING outside the
    bundle. Structural validity and evaluation completion are two separate states and both are written:
    a correctly accounted INCOMPLETE evaluation in an intact bundle is a valid deliverable and exits 0.
    A list that omits a required file cannot prove completeness, so the chain is walked from the
    bundle's own ledger and receipts rather than from what the manifest chose to list."""
    d = Path(a.dir)
    defects: list[dict] = []
    missing: list[str] = []
    mismatched: list[dict] = []
    ok = 0
    memo = HashMemo()

    def defect(kind: str, path: str | None, detail: str) -> None:
        defects.append({"kind": kind, "path": path, "detail": detail})

    # (1) the manifest itself
    manifest = load_json(d / "manifest.json")
    schema_supported = False
    if not isinstance(manifest, dict):
        manifest = {}
        defect("manifest-missing", "manifest.json",
               f"{d / 'manifest.json'} is absent or not readable as JSON: this is not a bundle.")
    else:
        if manifest.get("bundleSchema") != BUNDLE_SCHEMA:
            defect("schema-unsupported", "manifest.json",
                   f"bundleSchema {manifest.get('bundleSchema')!r} is not {BUNDLE_SCHEMA}: this bundle "
                   f"predates evidence-chain verification; rebuild it from its run.")
        else:
            schema_supported = True
        absent_keys = [k for k in MANIFEST_KEYS if k not in manifest]
        if absent_keys:
            defect("manifest-incomplete", "manifest.json",
                   "the manifest does not carry " + ", ".join(absent_keys))

    if schema_supported:
        # (2) required paths, then every listed evidence row against its recorded hash
        for rel in manifest.get("required") or []:
            if not (d / rel).exists():
                defect("required-missing", rel, "a path the manifest declares required is not present")
        for e in manifest.get("evidence") or []:
            p = d / e["path"]
            if not p.exists():
                missing.append(e["path"])
                continue
            got = memo.sha256(p)
            if got != e["sha256"]:
                mismatched.append({"path": e["path"], "recorded": e["sha256"], "found": got})
            else:
                ok += 1

        # (3) the plan's own immutability and the planId every document agrees on
        plan = load_json(d / "plan.json") or {}
        recorded_plan = (d / "plan.sha256")
        if not (d / "plan.json").exists() or not recorded_plan.exists():
            defect("plan-missing", "plan.json", "the bundle carries no plan and its recorded hash")
        elif memo.sha256(d / "plan.json") != recorded_plan.read_text(encoding="utf-8").strip():
            defect("plan-changed", "plan.json", "plan.json does not hash to plan.sha256")
        ledger_doc = load_json(d / "ledger.json") or {}
        disp = load_json(d / "disposition.json") or {}
        ids = {"manifest": manifest.get("planId"), "plan": plan.get("planId"),
               "ledger": ledger_doc.get("planId"), "disposition": disp.get("planId")}
        if len({v for v in ids.values()}) > 1:
            defect("planid-disagreement", None,
                   "the manifest, plan, ledger and disposition do not agree on planId: " + canonical(ids))

        # (4) the registry and scope copies against the hashes the plan froze
        for rel, want, label in ((manifest.get("registry") or "registry/check-registry.json",
                                  (plan.get("registry") or {}).get("sha256"), "registry"),
                                 (manifest.get("scope") or "scope/scope.json",
                                  (plan.get("scope") or {}).get("sha256"), "scope")):
            if want is None:
                continue  # a plan made without --scope froze no scope hash
            if not (d / rel).exists():
                defect(f"{label}-missing", rel, f"the plan froze a {label} hash but the copy is absent")
            elif memo.sha256(d / rel) != want:
                defect(f"{label}-mismatch", rel,
                       f"the {label} copy hashes {str(memo.sha256(d / rel))[:12]}, the plan froze "
                       f"{str(want)[:12]}")

        # (5) the receipt-to-evidence chain, walked from the LEDGER
        listed = {e["path"] for e in (manifest.get("evidence") or [])}
        for k, e in (ledger_doc.get("entries") or {}).items():
            if e.get("status") not in TERMINAL_OK:
                continue
            rec = load_json(d / "receipts" / f"{k}.json")
            if not isinstance(rec, dict):
                defect("receipt-missing", f"receipts/{k}.json",
                       f"the ledger records {k} as {e.get('status')} and the bundle carries no receipt")
                continue
            if e.get("receiptId") and rec.get("receiptId") != e.get("receiptId"):
                defect("receipt-mismatch", f"receipts/{k}.json",
                       f"the ledger names receipt {e.get('receiptId')}, the file is {rec.get('receiptId')}")
            if not (rec.get("validation") or {}).get("accepted"):
                defect("receipt-mismatch", f"receipts/{k}.json",
                       "a terminal-ok ledger entry whose receipt was not accepted")
            # Its outputs AND the run-internal emits it was compared against: a diff whose arm emit the
            # bundle omits cannot be re-read as a diff of anything.
            chain = [(o.get("path"), o.get("sha256"), True) for o in (rec.get("outputs") or [])]
            consumed = rec.get("consumed") or {}
            if consumed.get("baseEmitPath"):
                chain.append((consumed["baseEmitPath"], consumed.get("baseEmitSha256"), False))
            for row in consumed.get("inputs") or []:
                chain.append((row.get("path"), row.get("sha256"), False))
            for path, recorded, is_output in chain:
                if not path:
                    continue
                rel = f"evidence/{path}"
                if not (d / rel).exists():
                    defect("evidence-missing", rel,
                           f"receipt {rec.get('receiptId')} records this file and the bundle omits it")
                    continue
                if recorded is not None and memo.sha256(d / rel) != recorded:
                    defect("evidence-changed", rel,
                           f"receipt {rec.get('receiptId')} recorded {str(recorded)[:12]}, the copy "
                           f"hashes {str(memo.sha256(d / rel))[:12]}")
                if is_output and rel not in listed:
                    defect("manifest-omits-required", rel,
                           "a receipt's required output is present but not listed in the manifest, so the "
                           "manifest cannot prove the bundle complete")
        for row in (disp.get("manualDecisions") or []):
            if not row.get("accepted"):
                continue
            for ev_row in row.get("evidence") or []:
                rel = f"evidence/{ev_row['path']}"
                if not (d / rel).exists():
                    defect("decision-evidence-missing", rel,
                           f"the accepted decision on {row.get('checkId')} cites evidence the bundle omits")

        # (6) the disposition must agree with the ledger it claims to be derived from
        entries = ledger_doc.get("entries") or {}
        ev = disp.get("evaluation") or {}
        for label, rows, want_detector in (("findings", ev.get("findings") or [], "finding"),
                                           ("completedNullChecks", ev.get("completedNullChecks") or [],
                                            "no-finding"),
                                           ("completedCatalogues", ev.get("completedCatalogues") or [],
                                            "catalogue")):
            for row in rows:
                key = Ledger.key(row.get("checkId"), row.get("armId"), row.get("part"))
                entry = entries.get(key)
                if not entry or entry.get("status") not in TERMINAL_OK \
                        or entry.get("detector") != want_detector:
                    defect("disposition-unsupported-claim", None,
                           f"disposition.evaluation.{label} names {key}, which the ledger does not hold "
                           f"as a terminal-ok {want_detector} entry")
        if ev.get("complete") and (ev.get("missing") or ev.get("unknownApplicability")):
            defect("disposition-complete-contradiction", "disposition.json",
                   "evaluation.complete is true while missing or unknownApplicability is non-empty")
        want_derived = derived_from(d, read_receipts(d))
        if disp.get("derivedFrom") != want_derived:
            defect("disposition-stale", "disposition.json",
                   f"the disposition was derived from ledger "
                   f"{str((disp.get('derivedFrom') or {}).get('ledgerSha256'))[:12]}, the bundle carries "
                   f"{str(want_derived['ledgerSha256'])[:12]}")

    structurally_valid = schema_supported and not defects
    complete = structurally_valid and not missing and not mismatched
    disp = load_json(d / "disposition.json") or {}
    ev = disp.get("evaluation") or {}
    verify = {"schemaVersion": SCHEMA_VERSION, "checkedUtc": utcnow(), "dir": str(d.resolve()),
              "bundleSchema": manifest.get("bundleSchema"), "planId": manifest.get("planId"),
              "bundle": {"schemaSupported": schema_supported, "structurallyValid": structurally_valid,
                         "complete": complete, "verified": ok,
                         "missing": missing, "mismatched": mismatched, "defects": defects},
              # READ from the bundle's disposition, never recomputed: verify-bundle says whether the
              # package is intact, not whether the evaluation was finished.
              "evaluation": {"complete": ev.get("complete"), "claim": ev.get("claim"),
                             "missing": ev.get("missing", [])},
              "note": "every path is bundle-relative, so a relocated bundle validates identically; bundle "
                      "integrity and evaluation completion are two states and both are written here"}
    write_json(d / "verify.json", verify)
    print(f"verify: {ok} file(s) verified, {len(missing)} missing, {len(mismatched)} mismatched, "
          f"{len(defects)} defect(s); bundle.complete: {complete}; "
          f"evaluation.complete: {ev.get('complete')}")
    for x in defects[:20]:
        print(f"   {x['kind']}: {x['path'] or '-'} — {x['detail']}", file=sys.stderr)
    return EXIT_OK if complete else EXIT_REFUSED_RESULT


# ------------------------------------------------------------------------------------ status --

def cmd_status(a) -> int:
    run = Path(a.run)
    plan, why = load_plan(run)
    if plan is None:
        return refuse(why)
    ledger = Ledger(run, plan)
    if ledger.refusal():
        return refuse(ledger.refusal())
    print_status(run, plan, ledger)
    return EXIT_OK


def print_status(run: Path, plan: dict, ledger: Ledger) -> None:
    rows = sorted(ledger.doc["entries"].values(),
                  key=lambda e: (e["checkId"], e["armId"], e.get("part") or ""))
    w1 = max([len(r["checkId"]) for r in rows] + [5])
    w2 = max([len(r["armId"]) for r in rows] + [3])
    print(f"plan {plan['planId']}   {len(rows)} ledger entr{'y' if len(rows) == 1 else 'ies'}")
    print(f"{'check'.ljust(w1)}  {'arm'.ljust(w2)}  {'part':8}  {'status':34}  detector")
    print(f"{'-' * w1}  {'-' * w2}  {'-' * 8}  {'-' * 34}  {'-' * 10}")
    for r in rows:
        print(f"{r['checkId'].ljust(w1)}  {r['armId'].ljust(w2)}  {(r.get('part') or '-'):8}  "
              f"{(r.get('status') or '?')[:34]:34}  {r.get('detector') or '-'}")
    counts = _counts(ledger)
    print("   " + ", ".join(f"{k}: {v}" for k, v in counts.items()))


# -------------------------------------------------------------------------------------- cli --

def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="evalrun", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("plan", help="derive and freeze the check plan")
    p.add_argument("--registry", required=True)
    p.add_argument("--scope")
    p.add_argument("--candidates", required=True)
    p.add_argument("--alt", action="append", help="evaluate only these candidate files (repeatable)")
    p.add_argument("--supplied", action="append",
                   help="PLUGIN[,PLUGIN...]=candidate-NN.json - an ESP-only supplied-combination arm")
    p.add_argument("--states", default="baseline")
    p.add_argument("--select", action="append", help="CHECK,... — narrows what RUNS, never what is PLANNED")
    p.add_argument("--exclude", action="append", metavar="CHECK=reason")
    p.add_argument("--opt-in", action="append", metavar="CHECK[,CHECK...]",
                   help="plan an opt-in check (registry optIn: true), such as the live runtime.state-switch-flip; "
                        "opting in makes it required for this run. Not opted in, it is listed not-opted-in and "
                        "never blocks")
    p.add_argument("--budget-minutes", type=float,
                   help="a wall-time DEADLINE per `run`/`resume` invocation, measured from when that "
                        "invocation opens its executor, after its plan/ledger/premise checks and before "
                        "re-validation (fractional allowed; 0 or absent = no budget). "
                        "No child is launched after it; a child still running at it has its whole launched "
                        "process tree stopped, confirmed within a 15 s cleanup allowance, and its checks are "
                        "recorded stopped(deadline) - never completed. `resume` gets a fresh deadline.")
    p.add_argument("--max-candidates", type=int)
    p.add_argument("--out", required=True, metavar="RUN")
    p.add_argument("--instance"); p.add_argument("--profile"); p.add_argument("--game")
    p.add_argument("--harness", default=str(HERE))
    p.add_argument("--probe-args", default="")
    p.add_argument("--probe-cmd", action="append", metavar="NAME=<argv prefix>",
                   help="stand a different program in for a probe (fixtures); frozen into the plan")
    p.add_argument("--diff-tolerance")
    p.add_argument("--no-reverse", action="store_true")
    p.set_defaults(fn=cmd_plan)

    p = sub.add_parser("run", help="execute the plan's supported checks")
    p.add_argument("--run", required=True)
    p.set_defaults(fn=cmd_run)

    p = sub.add_parser("resume", help="re-validate, reuse what is unchanged, redo what is not")
    p.add_argument("--run", required=True)
    p.add_argument("--rebuild-ledger", action="store_true",
                   help="rebuild ledger.json from journal.jsonl and the receipts on disk")
    p.set_defaults(fn=cmd_resume)

    p = sub.add_parser("import-results", help="bind external MCP-only evidence to one planned arm")
    p.add_argument("--run", required=True)
    p.add_argument("--file", required=True,
                   help="schema-1 import result naming identity, coverage and hashed raw request/response, and "
                        "its origin: fresh (every MCP initialize carries clientInfo.name evalrun:<planId>:<armId>) "
                        "or reused with reusedFrom {planId, armId, resultSha256}; refused while the check's "
                        "prerequisite is not settled on the arm (§4.8)")
    p.set_defaults(fn=cmd_import_results)

    p = sub.add_parser("finalize", help="derive the disposition from the ledger; never from a claim")
    p.add_argument("--run", required=True)
    p.set_defaults(fn=cmd_finalize)

    p = sub.add_parser("bundle", help="assemble one evidence bundle")
    p.add_argument("--run", required=True)
    p.add_argument("--out")
    p.add_argument("--media")
    p.set_defaults(fn=cmd_bundle)

    p = sub.add_parser("verify-bundle", help="recompute every hash from the bundle-relative paths")
    p.add_argument("dir")
    p.set_defaults(fn=cmd_verify_bundle)

    p = sub.add_parser("status", help="print the ledger as a table")
    p.add_argument("--run", required=True)
    p.set_defaults(fn=cmd_status)
    return ap


def main(argv=None) -> int:
    a = build_parser().parse_args(argv)
    try:
        return a.fn(a)
    except KeyboardInterrupt:
        print("interrupted: the journal is the recovery source; `resume --run RUN` picks up from it",
              file=sys.stderr)
        return EXIT_REFUSED_INPUT


if __name__ == "__main__":
    sys.exit(main())
