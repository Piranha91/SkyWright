"""evalrun - what stays in Python of the evaluation controller: enriching a bundle.

    python evalrun.py enrich --bundle DIR --kind record-rows|names|order-variant|order-geometry [...]

What this is for. An evaluation is planned, run, finalized and bundled by the built-in controller, runcontrol
(_geometry-harness/runcontrol; `runcontrol plan|run|resume|import-results|finalize|bundle|population`), which owns those
rules, and `runcontrol verify-bundle`, which recomputes every hash from the bundle-relative paths. One command stays here
until it is ported: `enrich`, which adds evidence derived after measurement to a bundle (record rows, names, the reversed
order's record view, an order sweep's geometry); it checks the bundle with `runcontrol verify-bundle` before and after it
writes (HOUSECARL_RUNCONTROL, else this checkout's build).
`configuration_fingerprint` is the one Python spelling of the configuration identity (the text of
`_shared/Configuration.cs`'s ConfigurationIdentity.Compute) for the Python tools that need it.
`standards/HOUSECARL_EVALUATION_BUNDLE.md` is the contract.

Exit codes: 0 ok; 2 usage or a refused input; 3 the bundle no longer verifies after enrich wrote.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
from runlog import Run  # noqa: E402

sys.path.insert(0, str(HERE))

# Exit codes, spelled once.
EXIT_OK = 0
EXIT_REFUSED_INPUT = 2
EXIT_REFUSED_RESULT = 3


# ------------------------------------------------------------------------------- small helpers --

def sha256_file(p: Path) -> str | None:
    try:
        h = hashlib.sha256()
        with open(p, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


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


def refuse(message: str) -> int:
    """A refused input says what was wrong and exits 2 — never a degraded run."""
    print(f"REFUSED: {message}", file=sys.stderr)
    return EXIT_REFUSED_INPUT


def exe_name(name: str) -> str:
    return f"{name}.exe" if os.name == "nt" else name


# ---------------------------------------------------------------------------------- the plan --

def arm_id(kind: str, stem: str, index: int) -> str:
    """Short, stable, and a path a receipt filename can live inside on Windows. The full candidate
    file name stays in the arm record; only the id is shortened."""
    return "baseline" if kind == "baseline" else stem


# -------------------------------------------------------------------------- run store (D9) --


#: What a missing or changed premise costs, in the refusal's words.
PREMISE_WORDS = {"registry": ("the checklist this run was derived from cannot be re-read",
                              "A checklist cannot change under a frozen plan."),
                 "scope": ("the applicability this plan froze cannot be re-derived",
                           "A scope change changes the checklist."),
                 "search": ("the candidate search this plan read cannot be re-read",
                            "A candidate search cannot change under a frozen plan.")}


def premise_file_refusal(plan: dict, label: str, nxt: str, named: str | None = None) -> str | None:
    """The one hash check of a frozen premise (`registry` or `scope`): the plan's stored file, or a file named on the
    command line in its place (`--registry` / `--scope`), must exist and hash to what the plan froze."""
    want = str((plan.get(label) or {}).get("sha256"))
    p = Path(named if named is not None else plan[label]["path"])
    lost, changed = PREMISE_WORDS[label]
    if not p.is_file():
        if named is not None:
            return f"{label}-missing: --{label} {p} does not exist. {nxt}"
        return f"{label}-missing: the plan's {label} {p} does not exist, so {lost}. {nxt}"
    got = sha256_file(p)
    if got != want:
        if named is not None:
            return f"{label}-mismatch: --{label} {p} hashes {str(got)[:12]}, the plan froze {want[:12]}. {nxt}"
        return f"{label}-changed: {p} now hashes {str(got)[:12]}, the plan recorded {want[:12]}. {changed} {nxt}"
    return None


#: Where bundle copies each premise.
PREMISE_COPIES = {"registry": "registry/check-registry.json", "scope": "scope/scope.json",
                  "search": "search/candidates.json"}


# ------------------------------------------------------------------------------------ enrich --

def cmd_enrich(a) -> int:
    """Evidence derived after measurement, added to a bundle (bundle standard §8): verified first, refused by name."""
    import bundleenrich
    d = Path(a.bundle)
    # the run history goes to the configured store, never into the bundle
    with Run("evalrun-enrich", stage_source="counted") as r:
        r.stage("enrich", total=1, unit="enrichments")
        code, why = verify_quiet(d)
        if why is not None:
            msg = f"enrich refused: {why}"
            print(f"REFUSED: {msg}", file=sys.stderr)
            r.refuse(msg, EXIT_REFUSED_INPUT)
            return EXIT_REFUSED_INPUT
        if code != EXIT_OK:
            msg = (f"enrich refused: {d} does not verify (see its verify.json); an enrichment is added only to an "
                   f"intact bundle")
            print(f"REFUSED: {msg}", file=sys.stderr)
            r.refuse(msg, EXIT_REFUSED_INPUT)
            return EXIT_REFUSED_INPUT
        bad = next((x for x in a.mod_name or [] if "=" not in x), None)
        if bad is not None:
            return refuse(f"--mod-name takes PLUGIN=NAME, got {bad!r}")
        if a.kind == "names":
            cmd, source = default_reader_cmd(a.reader_cmd)
            st = names_step(d, cmd, source, mod_names(a.mod_name), reader_env(a.reader_env))
            if st["status"] == "refused":
                msg = f"enrich refused: {st['sentence']}"
                print(f"REFUSED: {msg}", file=sys.stderr)
                r.refuse(msg, EXIT_REFUSED_INPUT)
                return EXIT_REFUSED_INPUT
            write_names_status(d, st)            # a bundle made with the step skipped or refused now says enriched
            out = st["result"]
        else:
            try:
                if a.kind == "order-geometry" and not a.sweep:
                    raise bundleenrich.Refused("--kind order-geometry needs --sweep, the ordersweep folder over the "
                                               "measured order and its reversal")
                out = (bundleenrich.enrich_order_variant(d) if a.kind == "order-variant"
                       else bundleenrich.enrich_order_geometry(d, [Path(x) for x in a.sweep]) if a.kind == "order-geometry"
                       else bundleenrich.enrich_record_rows(d))
            except bundleenrich.Refused as ex:
                msg = f"enrich refused: {ex}"
                print(f"REFUSED: {msg}", file=sys.stderr)
                r.refuse(msg, EXIT_REFUSED_INPUT)
                return EXIT_REFUSED_INPUT
        code, why = verify_quiet(d)
        if code != EXIT_OK:
            msg = f"enrich wrote {a.kind} but the bundle no longer verifies; " + (why or "see its verify.json")
            print(f"REFUSED: {msg}", file=sys.stderr)
            r.refuse(msg, EXIT_REFUSED_RESULT)
            return EXIT_REFUSED_RESULT
        r.advance()
        print(json.dumps(out))
        return EXIT_OK


def verify_quiet(d: Path) -> tuple[int | None, str | None]:
    """`runcontrol verify-bundle d`, the bundle check's one owner, which writes d/verify.json: (exit code, None), or
    (None, one sentence) when the controller could not be started."""
    import evalpipeline
    try:
        argv = evalpipeline.verify_bundle_argv(None, d)
    except evalpipeline.Refusal as ex:
        return None, str(ex).rstrip(".")
    try:
        return subprocess.run(argv, capture_output=True, text=True, timeout=900).returncode, None
    except (OSError, subprocess.SubprocessError) as ex:
        return None, f"runcontrol verify-bundle could not run on {d}: {ex}"


#: The names enrichment's record reader when --reader-cmd names none (a command line, as --reader-cmd takes).
READER_ENV = "HOUSECARL_RECORD_READER_CMD"


def default_reader_cmd(explicit: str | None) -> tuple[str | None, str]:
    """The record reader the names enrichment starts: --reader-cmd, else HOUSECARL_RECORD_READER_CMD, else this
    checkout's built houseCARL server. Returns (command, where it came from), or (None, one sentence why none)."""
    if explicit:
        return explicit, "--reader-cmd"
    env = (os.environ.get(READER_ENV) or "").strip()
    if env:
        return env, READER_ENV
    exe = HERE.parent / "external" / "houseCARL" / "src" / "housecarl-mcp" / "bin" / "Release" / "net9.0" / exe_name("housecarl-mcp")
    if exe.is_file():
        return f'"{exe}"', "checkout"
    return None, (f"no record reader was given (--reader-cmd or {READER_ENV}) and this checkout's houseCARL server is "
                  f"not built at {exe}.")


def mod_names(pairs) -> dict:
    """--mod-name PLUGIN=NAME pairs (the caller has refused a pair without '=')."""
    return {k.strip(): v.strip() for k, v in (x.split("=", 1) for x in pairs or [] if "=" in x)}


def reader_env(pairs) -> dict:
    return dict(x.split("=", 1) for x in pairs or [] if "=" in x)


def names_step(d: Path, cmd: str | None, source: str, mods: dict, env: dict) -> dict:
    """Run the names enrichment on bundle d and say what happened in one sentence: enriched, partial (something the
    rows name could not be read) or refused. Never raises for a refusal; nothing of the names is written on one."""
    import bundleenrich
    t0 = time.monotonic()

    def done(status, sentence, result=None, counts=None):
        return {"status": status, "sentence": " ".join(str(sentence).split()), "reader": source if cmd else None,
                "counts": counts, "seconds": round(time.monotonic() - t0, 1), "result": result}

    if cmd is None:
        return done("refused", "The names enrichment was not run because " + source)
    try:
        out = bundleenrich.enrich_names(d, cmd, mods, env or None)
    except bundleenrich.Refused as ex:
        return done("refused", f"The names enrichment was refused: {str(ex).rstrip('.')}.")
    except (OSError, ValueError) as ex:
        return done("refused", f"The names enrichment could not start or read with its record reader ({cmd}): "
                               f"{str(ex).rstrip('.') or type(ex).__name__}.")
    c = out.get("counts") or {}
    if c.get("objectsUnread") or c.get("basesUnread"):
        return done("partial", f"The names enrichment could not read {c.get('objectsUnread')} of the "
                               f"{c.get('objectsNamed')} objects the rows name and {c.get('basesUnread')} of their "
                               f"{c.get('bases')} base records (listed under unread in enrichment/names/names.json), so "
                               f"the report counts their clashes as cannot-tell.", out, c)
    return done("enriched", f"The names enrichment read all {c.get('objectsNamed')} objects the rows name and their "
                            f"{c.get('bases')} base records.", out, c)


def write_names_status(d: Path, st: dict) -> dict:
    """Record the names step's statement in the bundle's manifest (`enrichmentStatus.names`), which verify-bundle
    checks against the files."""
    m = load_json(d / "manifest.json") or {}
    m.setdefault("enrichmentStatus", {})["names"] = {k: v for k, v in st.items() if k != "result"}
    write_json(d / "manifest.json", m)
    return st


# -------------------------------------------------------------------------------------- cli --

def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="evalrun", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("enrich", help="add evidence derived after measurement to a bundle (record rows, names)")
    p.add_argument("--bundle", required=True)
    p.add_argument("--kind", required=True, choices=("record-rows", "names", "order-variant", "order-geometry"))
    p.add_argument("--sweep", action="append", default=[],
                   help="order-geometry: an ordersweep out folder over the measured order and its reversal (repeatable)")
    p.add_argument("--reader-cmd", help="names: the houseCARL MCP server command line to read records with (default: "
                                        f"{READER_ENV}, else this checkout's built server)")
    p.add_argument("--reader-env", action="append", metavar="KEY=VALUE",
                   help="names: an environment variable for the reader, such as a private HOUSECARL_DATA_DIR")
    p.add_argument("--mod-name", action="append", metavar="PLUGIN=NAME", help="names: a mod's declared ordinary name")
    p.set_defaults(fn=cmd_enrich)
    return ap


def main(argv=None) -> int:
    a = build_parser().parse_args(argv)
    try:
        return a.fn(a)
    except KeyboardInterrupt:
        print("interrupted", file=sys.stderr)
        return EXIT_REFUSED_INPUT


if __name__ == "__main__":
    sys.exit(main())
