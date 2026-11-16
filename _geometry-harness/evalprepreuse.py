"""Reuse of an earlier manual run's preparation (the census, the load-order setup and the load-order checks).

A manual run (an in-place entry: one profile of an MO2 instance, a selection of its mod folders) spends most of a short
run preparing. A later run in the same Work Folder may take that preparation over only when everything it was built
from is the same: the instance, profile, game and selection; every file of the profile's own folder; every file of
each mod folder either arm loads, the overwrite folder and the game's Data folder (relative path, size and mtime); and
the code that built it (the pipeline's own scripts and the scopeprobe build). Anything else, or any doubt, runs fresh.
The plan stage and everything after it always run.

The key is written into every manual run folder (`preparation-key.json`); the reusing run copies the earlier run's
stage folders into its own, with the earlier folder's path rewritten to its own, and says which run it reused or why it
could not.
"""
import hashlib
import json
import os
import shutil
from pathlib import Path

KEY_FILE = "preparation-key.json"
STAGES = ("census", "prepare", "scope")
TEXT_SUFFIXES = {".json", ".txt", ".log", ".jsonl"}


def _sha_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _listing(folder: Path) -> str | None:
    """sha256 of a folder's whole tree, by relative path, size and mtime; None when the folder is absent."""
    if not folder.is_dir():
        return None
    rows = []
    for root, dirs, files in os.walk(folder):
        dirs.sort()
        for f in sorted(files):
            p = os.path.join(root, f)
            try:
                st = os.stat(p)
            except OSError:
                rows.append([os.path.relpath(p, folder).replace("\\", "/").lower(), None, None])
                continue
            rows.append([os.path.relpath(p, folder).replace("\\", "/").lower(), st.st_size, st.st_mtime_ns])
    return hashlib.sha256(json.dumps(rows, separators=(",", ":")).encode("utf-8")).hexdigest()


def key(instance: Path, profile: str, game: Path, selection, arms, tools: dict) -> dict:
    """The preparation key: four parts compared separately, so a refusal can name the part that differs."""
    pdir = Path(instance) / "profiles" / profile
    profile_files = {f.name.lower(): _sha_file(f) for f in sorted(pdir.iterdir()) if f.is_file()} if pdir.is_dir() else {}
    mods = sorted({str(m) for a in arms for m in a.get("mods") or []}, key=str.lower)
    folders = {}
    for m in mods:
        folder = Path(m) if os.path.isabs(m) else Path(instance) / "mods" / m
        folders["mod:" + m.lower()] = _listing(folder)
    folders["overwrite"] = _listing(Path(instance) / "overwrite")
    folders["data"] = _listing(Path(game) / "Data")
    return {"schema": 1,
            "identity": {"instance": str(instance).replace("\\", "/").lower(), "profile": profile.lower(),
                         "game": str(game).replace("\\", "/").lower(),
                         "selection": sorted(str(s).lower() for s in selection)},
            "profileFiles": profile_files, "folders": folders, "tools": tools}


def tools_digest(harness: Path, scopeprobe_exe: Path) -> dict:
    """The code the preparation runs: the pipeline and controller scripts and the scopeprobe build next to its exe."""
    out = {}
    for name in ("evalpipeline.py", "evalrun.py", "evalprepreuse.py"):
        p = harness / name
        out[name] = _sha_file(p) if p.is_file() else None
    folder = scopeprobe_exe.parent
    for name in ("scopeprobe.dll", "scopeprobe.exe"):
        p = folder / name
        out["scopeprobe/" + name] = _sha_file(p) if p.is_file() else None
    return out


PARTS = {"identity": "a different instance, profile, game or selection",
         "profileFiles": "a profile file changed (modlist.txt, plugins.txt, loadorder.txt or another file in the profile folder)",
         "folders": "a file in a mod folder the run loads, the overwrite folder or the game's Data folder changed",
         "tools": "the pipeline or the scopeprobe build changed"}


def _differs(a: dict, b: dict) -> str | None:
    for part in ("identity", "profileFiles", "folders", "tools"):
        if a.get(part) != b.get(part):
            return part
    return None


def find(work_folder: Path, this_out: Path, mine: dict, arms) -> tuple[Path | None, str]:
    """The newest earlier run folder under the Work Folder whose key equals this run's and whose census, setup and
    load-order checks were accepted and resolve the same arms; else None and the sentence why."""
    candidates = []
    for d in Path(work_folder).iterdir() if Path(work_folder).is_dir() else []:
        run = d / "run"
        if run.resolve() == Path(this_out).resolve() or not (run / KEY_FILE).is_file():
            continue
        candidates.append(run)
    if not candidates:
        return None, "no earlier run in the Work Folder recorded its preparation"
    candidates.sort(key=lambda r: (r / KEY_FILE).stat().st_mtime, reverse=True)
    nearest = None
    for run in candidates:
        try:
            theirs = json.loads((run / KEY_FILE).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        part = _differs(mine, theirs)
        if part is not None:
            if nearest is None or (part != "identity" and nearest[1] == "identity"):
                nearest = (run, part)
            continue
        try:
            stages = json.loads((run / "stages.json").read_text(encoding="utf-8")).get("stages") or {}
            their_arms = json.loads((run / "arms.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            nearest = nearest or (run, None)
            continue
        if not all((stages.get(s) or {}).get("accepted") for s in STAGES):
            nearest = nearest or (run, None)
            continue
        if json.loads(json.dumps(arms)) != their_arms:
            nearest = nearest or (run, None)
            continue
        return run, ""
    if nearest is None:
        return None, "no earlier run in the Work Folder has a readable preparation"
    run, part = nearest
    if part is None:
        return None, f"{run.parent.name} has the same inputs, but its preparation did not finish or resolved other load orders"
    if part == "identity":
        return None, "no earlier run in the Work Folder used this profile and selection"
    return None, f"{run.parent.name} used this profile and selection, but {PARTS[part]}"


def _pairs(old: Path, new: Path):
    pairs = []
    for o, n in ((str(old), str(new)), (str(old).replace("\\", "/"), str(new).replace("\\", "/"))):
        pairs.append((o.replace("\\", "\\\\"), n.replace("\\", "\\\\")))
        pairs.append((o, n))
    return [(o, n) for o, n in pairs if o != n]


def _swap(text: str, pairs) -> str:
    for o, n in pairs:
        text = text.replace(o, n)
    return text


def seed(old_run: Path, new_run: Path) -> dict:
    """Copy the earlier run's census, setup and load-order-check folders into this run folder, the earlier folder's
    path rewritten to this one's in every text file, and return each stage's earlier ledger record rewritten the same
    way (the caller re-records it under this run's own inputs)."""
    old_run, new_run = Path(old_run).resolve(), Path(new_run).resolve()
    pairs = _pairs(old_run, new_run)
    records = json.loads((old_run / "stages.json").read_text(encoding="utf-8"))["stages"]
    out = {}
    for s in STAGES:
        dst = new_run / s
        if dst.exists():
            shutil.rmtree(dst)
        shutil.copytree(old_run / s, dst)
        for p in dst.rglob("*"):
            if p.is_file() and p.suffix.lower() in TEXT_SUFFIXES:
                try:
                    text = p.read_text(encoding="utf-8")
                except (OSError, UnicodeDecodeError):
                    continue
                changed = _swap(text, pairs)
                if changed != text:
                    p.write_text(changed, encoding="utf-8")
        rec = json.loads(_swap(json.dumps(records[s]), pairs))
        for path in rec.get("outputs") or {}:
            try:
                Path(path).resolve().relative_to(new_run)
            except ValueError:
                raise ValueError(f"stage {s} of {old_run} names an output outside its run folder: {path}")
        out[s] = rec
    return out
