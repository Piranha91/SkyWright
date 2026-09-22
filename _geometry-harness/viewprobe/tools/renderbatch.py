"""Batch planning primitives for viewprobe's opt-in static-worklist campaign path.

This module never rewrites a worklist.  Every chunk selects coordinates from the original file,
so renderer filenames, report fields, replay text and caller destinations keep their original
cell/shot identity.  The renderer selection/progress flags are intentionally still RED.

The public CLI reads a JSON manifest, prints/writes a frozen plan and, with ``mode: run``, executes
one fully concrete worklist/argv/output group. It deliberately reports ``actualConcurrency: 1``
and refuses multiple groups until the effective-input and composite campaign adapter is complete.
Plan output includes the computed readiness SHA-256 that a subsequent run must present. The run
path is unavailable with the current renderer until its separately reviewed selection/progress
hook is integrated.
"""
from __future__ import annotations

import hashlib
import json
import math
import re
import sys
import os
import argparse
import shutil
import ctypes
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from runlog import Runner                                      # noqa: E402,F401
from renderlimit import Need, plan_limit, read_graphics_free, read_system_free  # noqa: E402,F401

KEY_TABLE = HERE / "renderbatch_keys.json"
LEVEL_COUNTS = {"process": 47, "configuration": 15, "cell": 39, "shot": 90, "none": 10}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest().upper()


def load_key_table(path=KEY_TABLE):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    rows = data.get("options")
    if not isinstance(rows, list):
        raise ValueError("invalidation key table has no options list")
    strategy = [r for r in rows if not r.get("operational")]
    operational = [r for r in rows if r.get("operational")]
    counts = {level: sum(r.get("invalidates") == level for r in strategy) for level in LEVEL_COUNTS}
    if len(strategy) != 201 or counts != LEVEL_COUNTS:
        raise ValueError(f"invalidation key table is incomplete: {len(strategy)} rows, {counts}")
    identities = [(r.get("surface"), r.get("name")) for r in rows]
    if len(set(identities)) != len(identities):
        raise ValueError("invalidation key table has duplicate surface/name rows")
    expected_ops = {("operational-cli", "--worklist-selection", "process"),
                    ("operational-cli", "--progress-dir", "none"),
                    ("operational-cli", "--worklist-readiness-sha256", "process")}
    got_ops = {(r.get("surface"), r.get("name"), r.get("invalidates")) for r in operational}
    if got_ops != expected_ops:
        raise ValueError(f"operational key rows differ: {sorted(got_ops)}")
    return data


def worklist_shape(path):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    cells = data.get("Cells", data.get("cells"))
    if not isinstance(cells, list) or not cells:
        raise ValueError("worklist has no Cells")
    out = []
    for ci, cell in enumerate(cells):
        shots = cell.get("Shots", cell.get("shots"))
        if not isinstance(shots, list) or not shots:
            raise ValueError(f"worklist cell {ci} has no Shots")
        for si, shot in enumerate(shots):
            out.append({"cell": ci, "shot": si,
                        "label": shot.get("Label", shot.get("label", ""))})
    return data, out


def selection(path, readiness_sha256, coordinates, source_map=None):
    _, all_rows = worklist_shape(path)
    valid = {(r["cell"], r["shot"]) for r in all_rows}
    coords = []
    for c, s in coordinates:
        if type(c) is not int or type(s) is not int:
            raise ValueError("selection coordinates must be integers")
        coords.append((c, s))
    if len(set(coords)) != len(coords):
        raise ValueError("selection has duplicate coordinates")
    if not coords or any(x not in valid for x in coords):
        raise ValueError("selection is empty or has an out-of-range coordinate")
    if len({c for c, _ in coords}) != 1:
        raise ValueError("one renderer launch may select shots from one original cell only")
    result = {"schema": "viewprobe-worklist-selection-v1",
            "worklistSha256": sha256(path), "readinessSha256": readiness_sha256,
            "selected": [{"cell": c, "shot": s} for c, s in coords]}
    if source_map is not None:
        rows = source_map.get("rows")
        by_coord = {(x["compositeCoordinate"]["cell"],x["compositeCoordinate"]["shot"]):x
                    for x in rows or []}
        if set(coords) - set(by_coord): raise ValueError("source map lacks a selected coordinate")
        result["sourceMapSha256"] = source_map["sha256"]
        result["sourceMapPath"] = source_map["sourceMapPath"]
        result["sourceMapIdentityAlgorithm"] = "sha256-file-bytes-v1"
        result["sources"] = [by_coord[x] for x in coords]
    return result


def compose_campaign(source_specs, composite_path, mapping_path, label="renderbatch composite"):
    """Create one immutable multi-shot worklist and exact source/caller map from one-shot inputs."""
    if not source_specs: raise ValueError("campaign has no sources")
    shots=[]; rows=[]; common_cell=None; common_top=None; common_cell_fields=None
    first_data=None; first_cell=None; scratches=set()
    for ordinal,spec in enumerate(source_specs):
        source=Path(spec["path"]).resolve()
        if sha256(source) != spec["sha256"]: raise ValueError(f"campaign source changed: {source}")
        data=json.loads(source.read_text(encoding="utf-8")); cells=data.get("Cells",data.get("cells"))
        if not isinstance(cells,list) or len(cells)!=1:
            raise ValueError(f"campaign source is not one-cell: {source}")
        cell=cells[0]; source_shots=cell.get("Shots",cell.get("shots"))
        cell_id=cell.get("Cell",cell.get("cell"))
        if not isinstance(source_shots,list) or len(source_shots)!=1:
            raise ValueError(f"campaign source is not one-shot: {source}")
        if common_cell is None: common_cell=cell_id
        if cell_id != common_cell: raise ValueError("campaign sources do not name one cell")
        top_fields={k:v for k,v in data.items() if k not in {"Label","label","Cells","cells"}}
        cell_fields={k:v for k,v in cell.items() if k not in {"Cell","cell","Shots","shots"}}
        if common_top is None:
            common_top,common_cell_fields=top_fields,cell_fields
            first_data,first_cell=data,cell
        if top_fields!=common_top or cell_fields!=common_cell_fields:
            raise ValueError("campaign sources have differing top-level/cell defaults")
        shot=source_shots[0]; shots.append(shot)
        artifacts=spec.get("callerArtifacts")
        if not isinstance(artifacts,list) or not artifacts:
            raise ValueError(f"campaign source lacks exact caller artifacts: {source}")
        output_stem=spec.get("outputStem"); scratch=spec.get("scratchDirectory")
        if not isinstance(output_stem,str) or not output_stem or Path(output_stem).name != output_stem:
            raise ValueError(f"campaign source lacks a safe explicit outputStem: {source}")
        if not isinstance(scratch,str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*",scratch):
            raise ValueError(f"campaign source lacks a safe explicit scratchDirectory: {source}")
        if scratch.lower() in scratches: raise ValueError("campaign scratch directories are duplicate")
        scratches.add(scratch.lower())
        rows.append({"ordinal":ordinal+1,"sourceWorklist":str(source),
            "sourceWorklistSha256":spec["sha256"],"sourceCoordinate":{"cell":0,"shot":0},
            "compositeCoordinate":{"cell":0,"shot":ordinal},
            "label":shot.get("Label",shot.get("label","")),
            "shotSha256":hashlib.sha256(canonical_value(shot).encode()).hexdigest().upper(),
            "outputStem":output_stem,"scratchDirectory":scratch,
            "callerArtifacts":artifacts})
    label_key="Label" if "Label" in first_data else "label"
    cells_key="Cells" if "Cells" in first_data else "cells"
    cell_key="Cell" if "Cell" in first_cell else "cell"
    shots_key="Shots" if "Shots" in first_cell else "shots"
    composite={label_key:label,**common_top,
        cells_key:[{cell_key:common_cell,**common_cell_fields,shots_key:shots}]}
    cp,mp=Path(composite_path),Path(mapping_path)
    for path in (cp,mp):
        tmp=path.with_name(path.name+".tmp")
        if path.exists() or tmp.exists(): raise FileExistsError(f"campaign output exists: {path}")
        path.parent.mkdir(parents=True,exist_ok=True)
    cp_tmp=cp.with_name(cp.name+".tmp"); cp_tmp.write_text(json.dumps(composite,indent=2)+"\n")
    cp_tmp.replace(cp)
    mapping={"schema":"viewprobe-composite-source-map-v1","sourceMapPath":str(mp.resolve()),
             "identityAlgorithm":"sha256-file-bytes-v1",
             "compositeWorklist":str(cp.resolve()),"compositeWorklistSha256":sha256(cp),"rows":rows}
    mp_tmp=mp.with_name(mp.name+".tmp"); mp_tmp.write_text(json.dumps(mapping,indent=2)+"\n"); mp_tmp.replace(mp)
    mapping["sha256"]=sha256(mp)
    return composite,mapping


def source_map_identity(mapping):
    return sha256(mapping["sourceMapPath"])


def publish_caller_artifacts(source_map, coordinate, raw_artifacts, publication_receipt):
    """Copy a validated composite shot to its frozen caller paths without renaming or overwrite."""
    map_path=Path(source_map["sourceMapPath"]); map_sha=sha256(map_path)
    if map_sha != source_map.get("sha256"):
        raise ValueError("source map file digest changed")
    disk_map=json.loads(map_path.read_text(encoding="utf-8"))
    if {k:v for k,v in source_map.items() if k!="sha256"} != disk_map:
        raise ValueError("in-memory source map differs from immutable file")
    rows=[x for x in disk_map["rows"] if
          (x["compositeCoordinate"]["cell"],x["compositeCoordinate"]["shot"])==tuple(coordinate)]
    if len(rows)!=1: raise ValueError("source map coordinate is missing or duplicate")
    row=rows[0]; expected=row["callerArtifacts"]
    if len(expected)!=len(raw_artifacts): raise ValueError("caller/raw artifact count differs")
    receipt=Path(publication_receipt); intent=receipt.with_name(receipt.name+".intent.json")
    plan=[]
    for raw,target in zip(raw_artifacts,expected):
        src=Path(raw["path"]); dst=Path(target["path"]); kind=raw["kind"]
        if kind!=target["kind"] or src.name!=dst.name:
            raise ValueError("caller artifact kind/basename differs from renderer source map")
        if not src.is_file() or raw.get("sha256")!=sha256(src):
            raise ValueError(f"raw artifact is missing or changed: {src}")
        plan.append({"source":str(src.resolve()),"target":str(dst.resolve()),"kind":kind,
                     "sha256":raw["sha256"]})
    if len({x["target"].lower() for x in plan})!=len(plan): raise ValueError("duplicate caller target")
    intent_doc={"schema":"viewprobe-caller-publication-intent-v1","sourceMapSha256":map_sha,
                "coordinate":{"cell":coordinate[0],"shot":coordinate[1]},"artifacts":plan}
    expected_published=[{"path":x["target"],"kind":x["kind"],"sha256":x["sha256"]} for x in plan]
    expected_receipt={"schema":"viewprobe-caller-publication-v1","sourceMapPath":source_map["sourceMapPath"],
         "sourceMapSha256":map_sha,"coordinate":{"cell":coordinate[0],"shot":coordinate[1]},
         "sourceWorklist":row["sourceWorklist"],"sourceWorklistSha256":row["sourceWorklistSha256"],
         "shotSha256":row["shotSha256"],"artifacts":expected_published}
    if receipt.exists():
        existing=json.loads(receipt.read_text())
        if existing!=expected_receipt: raise ValueError("publication receipt identity/artifact set differs")
        if all(Path(x["path"]).is_file() and sha256(x["path"])==x["sha256"] for x in existing["artifacts"]):
            return existing
        raise ValueError("publication receipt artifacts are stale")
    intent_preexisting=intent.exists()
    if intent_preexisting:
        if json.loads(intent.read_text())!=intent_doc: raise ValueError("publication intent differs")
    else:
        collisions=[x["target"] for x in plan if Path(x["target"]).exists()]
        if collisions: raise FileExistsError(f"foreign caller artifacts exist before intent: {collisions}")
        intent.parent.mkdir(parents=True,exist_ok=True)
        with open(intent,"x",encoding="utf-8") as stream: json.dump(intent_doc,stream,indent=2)
    published=[]
    for item in plan:
        src=Path(item["source"]); dst=Path(item["target"]); tmp=dst.with_name(dst.name+".tmp")
        if dst.exists():
            if sha256(dst)==item["sha256"]: published.append({"path":str(dst),"kind":item["kind"],"sha256":item["sha256"]}); continue
            raise FileExistsError(f"foreign caller artifact exists: {dst}")
        if tmp.exists(): raise FileExistsError(f"caller temporary exists: {tmp}")
        dst.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(src,tmp)
        if sha256(tmp)!=item["sha256"]: raise IOError(f"caller copy digest differs: {dst}")
        try: os.link(tmp,dst)
        except FileExistsError: raise FileExistsError(f"caller artifact raced publication: {dst}")
        finally:
            if tmp.exists(): tmp.unlink()
        published.append({"path":str(dst.resolve()),"kind":item["kind"],"sha256":sha256(dst)})
    tmp=receipt.with_name(receipt.name+".tmp")
    if tmp.exists(): raise FileExistsError(f"publication receipt temporary exists: {tmp}")
    doc={**expected_receipt,"artifacts":published}
    tmp.write_text(json.dumps(doc,indent=2)+"\n"); os.link(tmp,receipt); tmp.unlink()
    return doc


def publish_completed_campaign(source_map, coordinates, receipt_dir, publication_receipts, *,
                               readiness_sha256, worklist_sha256, expected_artifacts):
    """Publish only renderer-validated receipts after the composite run completes."""
    if not isinstance(publication_receipts,dict):
        raise ValueError("composite run requires publicationReceipts")
    validated=[]
    for coord in sorted(coordinates):
        key=f"{coord[0]},{coord[1]}"
        if key not in publication_receipts:
            raise ValueError(f"missing publication receipt path for {coord}")
        raw_path=Path(receipt_dir)/f"c{coord[0]+1}s{coord[1]+1}.json"
        if coord not in expected_artifacts:
            raise ValueError(f"missing exact expected artifacts for publication {coord}")
        ok, raw=validate_receipt(raw_path,readiness_sha256=readiness_sha256,
            worklist_sha256=worklist_sha256,coordinate=coord,
            expected_artifacts=expected_artifacts[coord])
        if not ok: raise ValueError(f"publication receipt {coord} is invalid: {raw}")
        validated.append((coord,raw["artifacts"],publication_receipts[key]))
    published=[]
    for coord,artifacts,receipt in validated:
        published.append(publish_caller_artifacts(source_map,coord,artifacts,receipt))
    return published


def require_publication_coordinate_closure(grouped_coords, expected_artifacts, publication_receipts):
    if not isinstance(publication_receipts,dict):
        raise ValueError("composite run requires publicationReceipts")
    try: publication_coords={tuple(int(x) for x in key.split(",")) for key in publication_receipts}
    except (TypeError,ValueError): raise ValueError("publication receipt coordinate is malformed")
    grouped=set(grouped_coords)
    if set(expected_artifacts)!=grouped or publication_coords!=grouped:
        raise ValueError("expectedArtifacts, grouped items and publicationReceipts coordinates differ")


def write_selection(path, document):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    if path.exists() or tmp.exists():
        raise FileExistsError(f"selection output already exists: {path if path.exists() else tmp}")
    tmp.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    tmp.replace(path)


def validate_receipt(path, *, readiness_sha256, worklist_sha256, coordinate, expected_artifacts):
    p = Path(path)
    try:
        row = json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError) as ex:
        return False, f"receipt unreadable: {ex}"
    expected = {"cell": coordinate[0], "shot": coordinate[1]}
    got_coord = row.get("coordinate")
    if row.get("schema") != "viewprobe-shot-progress-v1":
        return False, "receipt schema differs"
    if row.get("status") != "complete" or type(row.get("exitCode")) is not int or row["exitCode"] != 0:
        return False, "receipt is not a zero-exit completion"
    if row.get("readinessSha256") != readiness_sha256 or row.get("worklistSha256") != worklist_sha256:
        return False, "receipt identity is stale or foreign"
    if not isinstance(got_coord, dict) or type(got_coord.get("cell")) is not int \
            or type(got_coord.get("shot")) is not int or got_coord != expected:
        return False, "receipt coordinate differs"
    artifacts = row.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        return False, "receipt has no artifacts"
    seen = set()
    actual = set()
    for artifact in artifacts:
        ap = Path(artifact.get("path", ""))
        kind = artifact.get("kind")
        if kind not in {"frame", "report", "mask", "console", "census", "depth", "navmesh",
                        "sidecar", "replay"} or not ap.is_file():
            return False, f"missing or unknown artifact: {ap}"
        key = str(ap.resolve()).lower()
        if key in seen:
            return False, f"duplicate artifact: {ap}"
        seen.add(key)
        actual.add((key, kind))
        if artifact.get("sha256") != sha256(ap):
            return False, f"artifact digest changed: {ap}"
    declared = {(str(Path(a["path"]).resolve()).lower(), a["kind"]) for a in expected_artifacts}
    if actual != declared:
        return False, "artifact set differs from declared exact outputs"
    masks = row.get("evidenceMasks")
    if not isinstance(masks, list) or any(not isinstance(x, str) for x in masks):
        return False, "evidence mask declaration is missing or malformed"
    artifact_masks = sorted(a["path"] for a in artifacts if a.get("kind") == "mask")
    if sorted(masks) != artifact_masks:
        return False, "evidence mask closure differs from artifact rows"
    return True, row


def resume_after_failed_chunk(ordered, valid_receipts):
    """Quarantine the first unfinished coordinate, resume later coordinates, retry culprit once."""
    missing = [tuple(x) for x in ordered if tuple(x) not in valid_receipts]
    if not missing:
        return {"later": [], "culpritAlone": []}
    return {"later": missing[1:], "culpritAlone": [missing[0]]}


def attempt_dir(root, logical_id, attempt):
    if not isinstance(logical_id, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", logical_id) \
            or logical_id in {".", ".."}:
        raise ValueError("logical id is not a safe path component")
    if type(attempt) is not int or attempt < 1:
        raise ValueError("attempt must be a positive integer")
    path = Path(root) / logical_id / f"attempt-{attempt:03d}"
    if Path(root).resolve() not in path.resolve().parents:
        raise ValueError("attempt path escapes its evidence root")
    path.mkdir(parents=True, exist_ok=False)
    return path


def finite_need(graphics_mib, system_mib, source="supplied"):
    values = (graphics_mib, system_mib)
    if any(not isinstance(v, (int, float)) or isinstance(v, bool) or not math.isfinite(v) or v <= 0
           for v in values):
        raise ValueError("per-process memory need must be finite and positive")
    return Need(float(graphics_mib), float(system_mib), source)


def option_key(row):
    return f"{row['surface']}:{row['name']}"


def canonical_value(value):
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
                          allow_nan=False)
    except (TypeError, ValueError) as ex:
        raise ValueError(f"identity value is not canonical JSON: {ex}") from ex


def group_campaign(items, table=None):
    """Validate the complete strategy identity and group original coordinates by invalidation."""
    table = table or load_key_table()
    strategy = [r for r in table["options"] if not r.get("operational")]
    required = {option_key(r) for r in strategy}
    grouped = {}
    for item in items:
        identity = item.get("identity")
        if not isinstance(identity, dict) or set(identity) != required:
            missing = sorted(required - set(identity or {})); extra = sorted(set(identity or {}) - required)
            raise ValueError(f"campaign identity differs; missing={missing}, extra={extra}")
        coord = item.get("coordinate")
        if not isinstance(coord, dict) or type(coord.get("cell")) is not int \
                or type(coord.get("shot")) is not int:
            raise ValueError("campaign coordinate must contain strict integer cell/shot")
        keys = {}
        for level in ("process", "configuration", "cell"):
            rows = [r for r in strategy if r["invalidates"] == level]
            keys[level] = tuple((option_key(r), canonical_value(identity[option_key(r)])) for r in rows)
        group_key = (keys["process"], keys["configuration"], keys["cell"])
        grouped.setdefault(group_key, []).append(item)
    groups = []
    for key, rows in grouped.items():
        rows.sort(key=lambda x: (x.get("size", 0), x["coordinate"]["shot"]))
        groups.append({"key": key, "items": rows})
    groups.sort(key=lambda g: repr(g["key"]))
    return groups


def chunks(rows, shots_per_process):
    if type(shots_per_process) is not int or shots_per_process < 1:
        raise ValueError("shots_per_process must be a positive integer")
    return [rows[i:i + shots_per_process] for i in range(0, len(rows), shots_per_process)]


def input_state(paths, environment=()):
    """Pin the current closed input set; callers pass every resolved file, including assemblies."""
    result = {"files": [], "environment": {}}
    resolved = []
    for value in paths:
        p = Path(value).resolve()
        if not p.is_file():
            raise ValueError(f"required input is missing: {p}")
        resolved.append(p)
    if len({str(p).lower() for p in resolved}) != len(resolved):
        raise ValueError("input closure contains duplicate paths")
    for p in sorted(resolved, key=lambda x: str(x).lower()):
        st = p.stat()
        result["files"].append({"path": str(p), "size": st.st_size,
                                "mtimeNs": st.st_mtime_ns, "sha256": sha256(p)})
    for name in sorted(environment):
        result["environment"][name] = os.environ.get(name)
    return result


def require_unchanged(expected, paths, environment=()):
    current = input_state(paths, environment)
    if current != expected:
        old = {x["path"]:x for x in expected.get("files",[])}
        new = {x["path"]:x for x in current.get("files",[])}
        for path in sorted(set(old)|set(new)):
            if old.get(path) != new.get(path):
                fields = [k for k in ("size","mtimeNs","sha256")
                          if (old.get(path) or {}).get(k) != (new.get(path) or {}).get(k)]
                raise RuntimeError(f"input file changed before launch/resume: {path}; fields={fields}")
        for name in sorted(set(expected.get("environment",{}))|set(current.get("environment",{}))):
            if expected.get("environment",{}).get(name) != current.get("environment",{}).get(name):
                raise RuntimeError(f"input environment changed before launch/resume: {name}")
        raise RuntimeError("input closure changed before launch/resume")
    return current


def process_commit_mib(pid):
    """Read current private commit for one owned Windows PID; None is explicitly unavailable."""
    if type(pid) is not int or pid <= 0 or os.name != "nt": return None
    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong),
                    ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
                    ("PrivateUsage", ctypes.c_size_t)]
    handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
    if not handle: return None
    try:
        counters = Counters(); counters.cb = ctypes.sizeof(counters)
        if not ctypes.windll.psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            return None
        return counters.PrivateUsage / (1024.0 * 1024.0)
    finally: ctypes.windll.kernel32.CloseHandle(handle)


def cap_observer(process_commit_reader, process_commit_cap_mib, *, system_reader=None,
                 system_available_floor_mib=None, graphics_reader=None, graphics_used_cap_mib=None):
    """Build a Runner observer; the injected public reader returns a finite MiB value or None."""
    if not isinstance(process_commit_cap_mib, (int, float)) or isinstance(process_commit_cap_mib, bool) \
            or not math.isfinite(process_commit_cap_mib) or process_commit_cap_mib <= 0:
        raise ValueError("process commit cap must be finite and positive")
    for name, limit in (("system available floor", system_available_floor_mib),
                        ("graphics used cap", graphics_used_cap_mib)):
        if limit is not None and (not isinstance(limit,(int,float)) or isinstance(limit,bool)
                or not math.isfinite(limit) or limit <= 0):
            raise ValueError(f"{name} must be finite and positive")
    def observe(event):
        kind = event.get("kind")
        if kind not in {"prelaunch","sample"}:
            return None
        if kind == "sample":
            value = process_commit_reader(event.get("ownedPid"))
            if not isinstance(value, (int, float)) or isinstance(value, bool) \
                    or not math.isfinite(value) or value < 0:
                return {"stop": "telemetry-unavailable", "message": "owned process commit unavailable",
                        "metrics": {"processCommitMiB": None}}
            if value >= process_commit_cap_mib:
                return {"stop": "process-commit-cap", "message": "owned process crossed commit cap",
                        "metrics": {"processCommitMiB": value,
                                    "processCommitCapMiB": process_commit_cap_mib}}
        if system_available_floor_mib is not None:
            reading = system_reader() if system_reader else None
            if reading is None or not reading.ok or reading.free_mib is None \
                    or not math.isfinite(reading.free_mib) or reading.free_mib < 0:
                return {"stop":"telemetry-unavailable","message":"system memory unavailable",
                        "metrics":{"systemAvailableMiB":None}}
            if reading.free_mib <= system_available_floor_mib:
                return {"stop":"system-memory-cap","message":"system available floor crossed",
                        "metrics":{"systemAvailableMiB":reading.free_mib,
                                   "systemAvailableFloorMiB":system_available_floor_mib}}
        if graphics_used_cap_mib is not None:
            reading = graphics_reader() if graphics_reader else None
            if reading is None or not reading.ok or reading.free_mib is None or reading.total_mib is None \
                    or not math.isfinite(reading.free_mib) or not math.isfinite(reading.total_mib) \
                    or reading.free_mib < 0 or reading.total_mib <= 0 or reading.free_mib > reading.total_mib:
                return {"stop":"telemetry-unavailable","message":"graphics memory unavailable",
                        "metrics":{"graphicsUsedMiB":None}}
            used = reading.total_mib - reading.free_mib
            if not math.isfinite(used) or used < 0:
                return {"stop":"telemetry-unavailable","message":"graphics memory invalid",
                        "metrics":{"graphicsUsedMiB":None}}
            if used >= graphics_used_cap_mib:
                return {"stop":"graphics-cap","message":"graphics used cap crossed",
                        "metrics":{"graphicsUsedMiB":used,"graphicsUsedCapMiB":graphics_used_cap_mib}}
        return None
    return observe


def next_resume(ordered, receipt_dir, *, readiness_sha256, worklist_sha256,
                expected_outputs):
    """Validate every coordinate's exact outputs; directory presence alone never advances resume."""
    valid = set()
    failures = {}
    for coord in ordered:
        c, s = coord
        receipt = Path(receipt_dir) / f"c{c + 1}s{s + 1}.json"
        outputs = expected_outputs(coord)
        ok, why = validate_receipt(receipt, readiness_sha256=readiness_sha256,
                                   worklist_sha256=worklist_sha256, coordinate=coord,
                                   expected_artifacts=outputs)
        if ok: valid.add(coord)
        elif receipt.exists(): failures[f"{c},{s}"] = why
    return resume_after_failed_chunk(ordered, valid), failures


def quarantine_outputs(coordinate, outputs, evidence_dir, *, authorized_output_roots):
    """Move partial caller artifacts into this immutable attempt before an exact-name retry."""
    evidence_dir = Path(evidence_dir) / "partial-artifacts" / f"c{coordinate[0]+1}s{coordinate[1]+1}"
    moved = []
    roots = [Path(x).resolve() for x in authorized_output_roots]
    if not roots:
        raise ValueError("quarantine needs an authorized output root")
    for row in outputs:
        source = Path(row["path"]).resolve()
        if not source.exists():
            continue
        matches = [(i, source.relative_to(root)) for i, root in enumerate(roots)
                   if source == root or root in source.parents]
        if len(matches) != 1:
            raise ValueError(f"partial artifact is outside or ambiguous across output roots: {source}")
        root_index, relative = matches[0]
        destination = evidence_dir / f"root-{root_index}" / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        if destination.exists():
            raise FileExistsError(f"partial-artifact audit destination exists: {destination}")
        before = sha256(source)
        temporary = destination.with_name(destination.name + ".tmp")
        if temporary.exists(): raise FileExistsError(f"partial-artifact temporary exists: {temporary}")
        shutil.copy2(source, temporary)
        if sha256(temporary) != before:
            raise IOError(f"partial-artifact copy digest differs: {source}")
        temporary.replace(destination)
        source.unlink()
        moved.append({"originalPath": str(source), "auditPath": str(destination),
                      "kind": row["kind"], "sha256": before})
    if moved:
        manifest = evidence_dir / "manifest.json"
        if manifest.exists():
            raise FileExistsError(f"partial-artifact manifest exists: {manifest}")
        manifest.write_text(json.dumps({"coordinate": {"cell": coordinate[0], "shot": coordinate[1]},
                                        "artifacts": moved}, indent=2) + "\n", encoding="utf-8")
    return moved


def execute_group(*, logical_id, ordered, worklist, readiness_sha256, receipt_dir,
                  expected_outputs, state, state_paths, state_environment,
                  attempt_root, argv_for, observer, shots_per_process, authorized_output_roots=None,
                  runner_name="renderbatch", job_cwd=None, job_environment=None, run_identity=None,
                  source_map=None):
    """Run one original-cell group, retaining every attempt and isolating one suspect once.

    `argv_for(selection_path, attempt_path)` supplies the renderer argv. This function deliberately
    launches one contained tree at a time so the closed input state is checked immediately before
    every launch and resume. Higher-level callers may schedule independent groups up to the
    separately computed renderlimit, but may not bypass this boundary.
    """
    ordered = [tuple(x) for x in ordered]
    if len(set(ordered)) != len(ordered) or not ordered:
        raise ValueError("group coordinates must be unique and nonempty")
    if not isinstance(logical_id,str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*",logical_id):
        raise ValueError("logical id is not a safe path component")
    logical_root = Path(attempt_root) / logical_id
    schedule_path = logical_root / "schedule.json"
    pre_schedule = json.loads(schedule_path.read_text(encoding="utf-8")) if schedule_path.exists() else None
    if pre_schedule and pre_schedule.get("inFlight"):
        return {"status":"recovery-refused","reason":"prior in-flight tree is not proven gone",
                "inFlight":pre_schedule["inFlight"],"attempts":[]}
    if pre_schedule and pre_schedule.get("terminalFailed") is not None:
        return {"status":"failed","failedCoordinate":tuple(pre_schedule["terminalFailed"]),
                "attempts":[]}
    require_unchanged(state, state_paths, state_environment)
    wh = sha256(worklist)
    run_identity = run_identity or hashlib.sha256(canonical_value({"state":state,
        "ordered":[list(x) for x in ordered],"readiness":readiness_sha256}).encode()).hexdigest().upper()
    if pre_schedule and (pre_schedule.get("schema") != "viewprobe-renderbatch-schedule-v1"
            or pre_schedule.get("ordered") != [list(x) for x in ordered]
            or pre_schedule.get("readinessSha256") != readiness_sha256
            or pre_schedule.get("worklistSha256") != wh
            or pre_schedule.get("runIdentity") != run_identity):
        raise RuntimeError(f"durable schedule identity differs: {schedule_path}")
    prior_valid = set()
    for coord in ordered:
        rp = Path(receipt_dir) / f"c{coord[0]+1}s{coord[1]+1}.json"
        if not rp.exists(): continue
        ok, why = validate_receipt(rp, readiness_sha256=readiness_sha256,
            worklist_sha256=wh, coordinate=coord,
            expected_artifacts=expected_outputs(coord))
        if not ok: raise RuntimeError(f"stale/foreign existing receipt {rp}: {why}")
        prior_valid.add(coord)
    remaining = [x for x in ordered if x not in prior_valid]
    if not remaining: return {"status": "complete", "attempts": []}
    logical_root.mkdir(parents=True, exist_ok=True)
    existing = [int(x.name.split("-")[-1]) for x in logical_root.glob("attempt-[0-9][0-9][0-9]")]
    terminal_failed = None
    if pre_schedule is not None:
        schedule = pre_schedule
        if schedule.get("schema") != "viewprobe-renderbatch-schedule-v1" \
                or schedule.get("ordered") != [list(x) for x in ordered] \
                or schedule.get("readinessSha256") != readiness_sha256 \
                or schedule.get("worklistSha256") != wh or schedule.get("runIdentity") != run_identity:
            raise RuntimeError(f"durable schedule identity differs: {schedule_path}")
        queue = [[tuple(x) for x in group] for group in schedule.get("pending",[])]
        queue = [[x for x in group if x not in prior_valid] for group in queue]
        queue = [group for group in queue if group]
        isolated = {tuple(x) for x in schedule.get("isolated",[])}
        attempt_number = max(int(schedule.get("lastAttempt",0)),max(existing,default=0))
        recover_inflight = schedule.get("inFlight")
        pending_flat=[x for group in queue for x in group]
        inflight_coords=[tuple(x) for x in (recover_inflight or {}).get("selected",[])]
        covered=set(pending_flat)|prior_valid|set(inflight_coords)
        if covered != set(ordered) or len(pending_flat) != len(set(pending_flat)) \
                or any(x not in ordered for x in isolated):
            raise RuntimeError(f"durable schedule coordinate partition differs: {schedule_path}")
    else:
        queue = chunks(remaining, shots_per_process)
        isolated, attempt_number = set(), max(existing, default=0)
        recover_inflight = None
    history = []
    revision_files=list((logical_root/"schedule-revisions").glob("[0-9][0-9][0-9][0-9].json"))
    revision=max((int(x.stem) for x in revision_files),default=-1)+1
    def save_schedule(in_flight=None):
        nonlocal revision
        doc={"schema":"viewprobe-renderbatch-schedule-v1","logicalId":logical_id,
             "ordered":[list(x) for x in ordered],"readinessSha256":readiness_sha256,
             "worklistSha256":wh,"runIdentity":run_identity,
             "pending":[[list(x) for x in group] for group in queue],
             "isolated":[list(x) for x in sorted(isolated)],"lastAttempt":attempt_number,
             "inFlight":in_flight,"terminalFailed":list(terminal_failed) if terminal_failed else None}
        revisions=logical_root/"schedule-revisions"; revisions.mkdir(exist_ok=True)
        snap=revisions/f"{revision:04d}.json"
        with open(snap,"x",encoding="utf-8") as stream: stream.write(json.dumps(doc,indent=2)+"\n")
        revision += 1
        tmp=schedule_path.with_name(schedule_path.name+".tmp")
        tmp.write_text(json.dumps(doc,indent=2)+"\n"); tmp.replace(schedule_path)
    if not schedule_path.exists(): save_schedule()
    if recover_inflight:
        return {"status":"recovery-refused","reason":"prior in-flight tree is not proven gone",
                "inFlight":recover_inflight,"attempts":[]}
    while queue:
        selected = queue.pop(0)
        require_unchanged(state, state_paths, state_environment)
        attempt_number += 1
        ad = attempt_dir(attempt_root, logical_id, attempt_number)
        save_schedule({"attempt":attempt_number,"selected":[list(x) for x in selected],
                       "attemptDirectory":str(ad)})
        sp = ad / "selection.json"
        write_selection(sp, selection(worklist, readiness_sha256, selected, source_map=source_map))
        result = Runner(runner_name, out_dir=ad / "runner").run([
            {"id": f"{logical_id}.attempt-{attempt_number}",
             "logicalId": logical_id, "attempt": attempt_number,
             "argv": argv_for(sp, ad), "cwd": job_cwd, "env": job_environment or {}}
        ], echo=False, observer=observer)[0]
        valid, invalid = [], []
        for coord in selected:
            rp = Path(receipt_dir) / f"c{coord[0] + 1}s{coord[1] + 1}.json"
            ok, why = validate_receipt(rp, readiness_sha256=readiness_sha256,
                                       worklist_sha256=wh, coordinate=coord,
                                       expected_artifacts=expected_outputs(coord))
            (valid if ok else invalid).append(coord if ok else (coord, why))
            if rp.exists() and not ok:
                raise RuntimeError(f"malformed/stale receipt created by child {rp}: {why}")
        record = {"attempt": attempt_number, "selected": selected, "result": result,
                  "validReceipts": valid, "invalidReceipts": invalid,
                  "attemptDirectory": str(ad)}
        (ad / "driver-result.json").write_text(json.dumps(record, indent=2) + "\n",
                                               encoding="utf-8")
        history.append(record)
        if not invalid:
            save_schedule()
            continue
        quarantined = []
        for coord, _ in invalid:
            quarantined.extend(quarantine_outputs(coord, expected_outputs(coord), ad,
                authorized_output_roots=authorized_output_roots or []))
        record["quarantinedArtifacts"] = quarantined
        (ad / "driver-result.json").write_text(json.dumps(record, indent=2) + "\n",
                                               encoding="utf-8")
        suspect = invalid[0][0]
        later = [coord for coord, _ in invalid[1:]]
        if later:
            queue.insert(0, later)
        if suspect in isolated:
            terminal_failed = suspect
            save_schedule()
            return {"status": "failed", "failedCoordinate": suspect, "attempts": history}
        isolated.add(suspect)
        queue.append([suspect])
        save_schedule()
    save_schedule()
    return {"status": "complete", "attempts": history}


def validate_run_shape(groups):
    if len(groups) != 1:
        return False, "this adapter currently requires one concrete worklist/argv/output group"
    if not groups[0].get("items"):
        return False, "run group is empty"
    return True, "one concrete group"


def result_exit_code(runs):
    return 0 if runs and all(x.get("status") == "complete" for x in runs) else 3


def main(argv=None):
    parser = argparse.ArgumentParser(description="Plan a validated viewprobe batch campaign")
    parser.add_argument("manifest")
    parser.add_argument("--json", dest="json_path")
    args = parser.parse_args(argv)
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    groups = group_campaign(manifest["items"])
    need = finite_need(manifest["need"]["graphicsMiB"], manifest["need"]["systemMiB"],
                       manifest["need"].get("source", "supplied"))
    plan = plan_limit(int(manifest["askedParallel"]), need, read_graphics_free(),
                      read_system_free(), float(manifest.get("reserveMiB", 2000)))
    state = input_state(manifest["statePaths"], manifest.get("stateEnvironment", []))
    result = {"schema":"viewprobe-renderbatch-plan-v1", "groups":len(groups),
              "shots":sum(len(g["items"]) for g in groups), "limit":plan.limit,
              "refused":plan.refused, "basis":plan.basis, "reason":plan.reason,
              "inputState":state}
    if "run" in manifest:
        frozen_run={k:v for k,v in manifest["run"].items() if k != "readinessSha256"}
        result["computedReadinessSha256"]=hashlib.sha256(canonical_value({"inputState":state,
            "run":frozen_run,"items":manifest["items"]}).encode()).hexdigest().upper()
    if manifest.get("mode", "plan") == "run":
        if plan.refused or plan.limit < 1:
            result["runs"] = []; result["runRefused"] = "memory plan refused"
        else:
            run = manifest["run"]
            shape_ok, shape_reason = validate_run_shape(groups)
            if not shape_ok: raise ValueError(shape_reason)
            required_caps=("processCommitCapMiB","systemAvailableFloorMiB","graphicsUsedCapMiB")
            if any(k not in run for k in required_caps):
                raise ValueError(f"run requires finite caps: {required_caps}")
            observer=cap_observer(process_commit_mib,run["processCommitCapMiB"],
                system_reader=read_system_free,system_available_floor_mib=run["systemAvailableFloorMiB"],
                graphics_reader=read_graphics_free,graphics_used_cap_mib=run["graphicsUsedCapMiB"])
            outputs={tuple(int(x) for x in key.split(",")):value
                     for key,value in run["expectedArtifacts"].items()}
            grouped_coords={(x["coordinate"]["cell"],x["coordinate"]["shot"])
                            for group in groups for x in group["items"]}
            def expected(coord):
                if coord not in outputs: raise ValueError(f"no expected artifacts for {coord}")
                return outputs[coord]
            required_flags={"--worklist-selection","--progress-dir","--worklist-readiness-sha256"}
            if not required_flags.issubset(set(run["argv"])):
                raise ValueError(f"run argv lacks operational flags: {sorted(required_flags-set(run['argv']))}")
            def argv_for(selection_path,attempt_path):
                return [str(x).replace("{selection}",str(selection_path))
                        .replace("{attempt}",str(attempt_path)).replace("{readiness}",computed_readiness)
                        for x in run["argv"]]
            runs=[]
            computed_readiness=result["computedReadinessSha256"]
            if run.get("readinessSha256") != computed_readiness:
                raise ValueError("caller readinessSha256 differs from computed durable run identity")
            source_map=None
            if run.get("sourceMapPath"):
                source_map=json.loads(Path(run["sourceMapPath"]).read_text(encoding="utf-8"))
                source_map["sha256"]=sha256(run["sourceMapPath"])
                if source_map["sha256"]!=run.get("sourceMapSha256"):
                    raise ValueError("run source map digest differs")
                require_publication_coordinate_closure(grouped_coords,outputs,
                    run.get("publicationReceipts"))
            for index,group in enumerate(groups):
                coords=[(x["coordinate"]["cell"],x["coordinate"]["shot"])
                        for x in group["items"]]
                runs.append(execute_group(logical_id=f"{run['logicalId']}.group-{index+1}",
                    ordered=coords,worklist=run["worklist"],readiness_sha256=run["readinessSha256"],
                    receipt_dir=run["receiptDir"],expected_outputs=expected,state=state,
                    state_paths=manifest["statePaths"],state_environment=manifest.get("stateEnvironment",[]),
                    attempt_root=run["attemptRoot"],argv_for=argv_for,observer=observer,
                    shots_per_process=run["shotsPerProcess"],authorized_output_roots=run["outputRoots"],
                    job_cwd=run.get("cwd"),job_environment=run.get("environment",{}),
                    run_identity=computed_readiness,source_map=source_map))
            result["runs"]=runs
            result["actualConcurrency"]=1
            if source_map is not None and all(x.get("status")=="complete" for x in runs):
                result["publications"]=publish_completed_campaign(source_map,outputs,
                    run["receiptDir"],run.get("publicationReceipts"),
                    readiness_sha256=run["readinessSha256"],worklist_sha256=sha256(run["worklist"]),
                    expected_artifacts=outputs)
    text = json.dumps(result, indent=2) + "\n"
    if args.json_path:
        out = Path(args.json_path); tmp = out.with_name(out.name+".tmp")
        if out.exists() or tmp.exists(): raise FileExistsError(f"plan output exists: {out}")
        tmp.write_text(text,encoding="utf-8"); tmp.replace(out)
    print(text,end="")
    if plan.refused: return 2
    if manifest.get("mode","plan") == "run": return result_exit_code(result.get("runs",[]))
    return 0


if __name__ == "__main__":
    try: raise SystemExit(main())
    except SystemExit: raise
    except BaseException:
        import traceback
        traceback.print_exc()
        raise SystemExit(1)
