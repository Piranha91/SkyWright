"""toolidentity — the one identity rule for the programs an evaluation runs.

evalrun freezes these identities into a plan, checks them before `run` / `resume` write anything, again when a child
launches and before its result is accepted, and binds them into every accepted receipt; ordersweep keys its cache by
the same digests. A library with no entry point: pure functions over files.

Three kinds, compared by digest, never by path (a path only locates the program):

- **package** — a Release folder: every top-level file except `*.pdb`, plus every file under `runtimes/`, each by its
  lower-cased relative path and sha256. Other subfolders are run-time output (viewprobe writes `shots/` there) and are
  not identity. Any other top-level file counts, even a stray text file. Digest `folder:<sha256>` over the sorted
  `path\\0sha256\\n` lines (byte-for-byte ordersweep's earlier `program_key`).
- **probe-cmd** — a `--probe-cmd NAME=TEXT` override: the command text and the bytes of every file its tokens name
  (split on whitespace, as the launch splits it, resolved against the current folder as the launch resolves them). A
  bare first token (`python`) is resolved through PATH and recorded as a locator only: interpreters, their standard
  libraries and the shared .NET runtime are outside identity. Files a named program imports are not followed. A command
  that names no file is `unverifiable`.
- **scripts** — the controller: its scripts by repository-relative name and sha256.

A file that exists but cannot be read raises `Unreadable`; it never becomes a value.
"""
from __future__ import annotations

import hashlib
import shutil
from pathlib import Path

#: The controller: every script whose code decides a run's, a resume's or an import's result.
CONTROLLER_FILES = ("_geometry-harness/evalrun.py", "_geometry-harness/evalregistry.py",
                    "_geometry-harness/evalsettings.py", "_geometry-harness/stategen.py",
                    "_geometry-harness/switchflip_scope.py", "_geometry-harness/switchflip_outcome.py",
                    "_geometry-harness/evallive.py", "_geometry-harness/ordersweep.py",
                    "_geometry-harness/toolidentity.py", "scripts/runlog.py")


class Unreadable(Exception):
    """A file that exists and could not be read: the code it holds cannot be identified."""

    def __init__(self, path: Path, error: Exception):
        super().__init__(f"{path}: {error}")
        self.path, self.error = Path(path), error


def file_sha256(p: Path) -> str:
    try:
        h = hashlib.sha256()
        with open(p, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError as ex:
        raise Unreadable(p, ex) from ex


def _digest(prefix: str, lines) -> str:
    h = hashlib.sha256()
    for ln in lines:
        h.update(ln.encode("utf-8"))
    return prefix + h.hexdigest()


def package(folder: Path) -> dict:
    """A Release folder's identity (see the module text). `state: absent` when the folder does not exist."""
    folder = Path(folder)
    if not folder.is_dir():
        return {"kind": "package", "locator": str(folder), "state": "absent", "digest": None, "files": []}
    built = [q for q in folder.iterdir() if q.is_file()] + [q for q in (folder / "runtimes").rglob("*") if q.is_file()]
    rows = sorted(({"path": q.relative_to(folder).as_posix().lower(), "file": q} for q in built
                   if q.suffix.lower() != ".pdb"), key=lambda r: r["path"])
    files = [{"path": r["path"], "sha256": file_sha256(r["file"])} for r in rows]
    return {"kind": "package", "locator": str(folder), "state": "present",
            "digest": _digest("folder:", (f"{f['path']}\0{f['sha256']}\n" for f in files)), "files": files}


def _pathlike(tok: str) -> bool:
    return "/" in tok or "\\" in tok


def probe_cmd(text: str) -> dict:
    """An override's identity (see the module text)."""
    toks = str(text).split()
    inputs, interpreter = [], None
    for i, tok in enumerate(toks):
        p = Path(tok)
        if i == 0 and not _pathlike(tok):
            found = shutil.which(tok)
            interpreter = str(Path(found).resolve()) if found else None
            continue
        if p.is_file():
            inputs.append({"token": i, "path": str(p.resolve()), "sha256": file_sha256(p)})
        elif _pathlike(tok):
            inputs.append({"token": i, "path": str(p), "sha256": None})
    rec = {"kind": "probe-cmd", "command": str(text), "interpreter": interpreter, "inputs": inputs}
    if not inputs:
        return {**rec, "state": "unverifiable", "digest": None}
    lines = [f"{text}\n"] + [f"{r['token']}\0{r['sha256'] or 'absent'}\n" for r in inputs]
    return {**rec, "state": "present", "digest": _digest("probe-cmd:", lines)}


def scripts(root: Path, names=CONTROLLER_FILES) -> dict:
    """The controller's identity: each named script under `root` (a repository root) by name and sha256."""
    root = Path(root)
    files = [{"name": n, "sha256": file_sha256(root / n) if (root / n).is_file() else None} for n in names]
    return {"kind": "scripts", "locator": str(root), "state": "present",
            "digest": _digest("scripts:", (f"{f['name']}\0{f['sha256'] or 'absent'}\n" for f in files)),
            "files": files}


def differences(old: dict, new: dict) -> list[str]:
    """What differs between two identities of one program, file by file, in plain words."""
    def rows(rec):
        if rec.get("kind") == "scripts":
            return {f["name"]: f["sha256"] for f in rec.get("files") or []}
        if rec.get("kind") == "probe-cmd":
            return {r["path"]: r["sha256"] for r in rec.get("inputs") or []}
        return {f["path"]: f["sha256"] for f in rec.get("files") or []}
    a, b = rows(old), rows(new)
    out = []
    for k in sorted(set(a) | set(b)):
        if k not in b or (b[k] is None and a.get(k) is not None):
            out.append(f"{k} removed")
        elif k not in a or (a[k] is None and b[k] is not None):
            out.append(f"{k} added")
        elif a[k] != b[k]:
            out.append(f"{k} changed {str(a[k])[:12]} -> {str(b[k])[:12]}")
    if old.get("kind") == "probe-cmd" and old.get("command") != new.get("command"):
        out.append(f"command text {old.get('command')!r} -> {new.get('command')!r}")
    return out


def short(digest) -> str:
    """A digest's first twelve hex characters, after its kind prefix."""
    s = str(digest or "none")
    return s.split(":", 1)[-1][:12]
