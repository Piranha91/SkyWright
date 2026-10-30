"""editkinds - the patch edit-kind vocabulary and its classifier, one place for every reader of patch edits.

An edit is one distinct change per record per kind (4.24 scorecard unit): a position is one MOVE, not three floats; a
disable is one DISABLE, not four field changes. The vocabulary is fixed:

  ADD            a record no plugin in the unpatched order defines; all its fields are absorbed
  DISABLE        the record ends disabled or deleted where the unpatched winner did not: Deleted or InitiallyDisabled
                 newly set, or an enable parent of the player with the opposite flag. Position, rotation, enable
                 parent and flag changes on the same record are part of the idiom and absorbed; after classification
                 every other kind on that record is absorbed too (`absorb_disabled`), since it has no effect in game
  ENABLE         InitiallyDisabled cleared where the winner had it set
  MOVE ROTATE SCALE BASE BOUNDS ENABLE-PARENT LINKED-REF LOCATION-REF OWNER RECORD-FLAGS
  FIELD:<name>   anything else, by its first path segment with list indices stripped; a location registration list
                 is one FIELD:<list> marked bookkeeping

Not edits (dropped before classification): record-header bookkeeping (version, form version, version control,
timestamps), a cell's or worldspace's child-group bookkeeping and child groups (GROUP_BOOKKEEPING), fields the caller
already showed equal to the unpatched winner (identity forwards), and container rows whose only differences are child
groups.

This module classifies field changes a caller has already read; it reads no plugin and decides nothing about whether
an edit repairs a clash. Lifted from the 4.24 scorecard's Group 1 adapter and common rules without changing a rule.
"""
from __future__ import annotations

import math

KINDS = ("ADD", "DISABLE", "ENABLE", "MOVE", "ROTATE", "SCALE", "BASE", "BOUNDS", "ENABLE-PARENT", "LINKED-REF",
         "LOCATION-REF", "OWNER", "RECORD-FLAGS")  # plus FIELD:<name>
SPATIAL_KINDS = frozenset({"ADD", "MOVE", "ROTATE", "SCALE", "DISABLE", "ENABLE", "BOUNDS", "BASE"})
PLAYER = "000014:Skyrim.esm"
POS_TOL = 1e-4
ROT_TOL = 1e-5
BOUNDS_TOL = 1e-4
FLAG_PERSISTENT = 0x400

HEADER_BOOKKEEPING = frozenset({
    "Header.version2", "Version2", "FormVersion", "VersionControl", "Timestamp", "TemporaryTimestamp",
    "TemporaryUnknownGroupData", "Header.formVersion", "Header.timestamp", "Header.versionControl"})

#: container bookkeeping: a cell's or worldspace's child-group timestamps and unknowns, and the child groups themselves
#: (their records are edits of their own); never an edit of the container (record-field-scope.json groupBookkeeping)
GROUP_BOOKKEEPING = frozenset({
    "Timestamp", "UnknownGroupData", "PersistentTimestamp", "PersistentUnknownGroupData", "TemporaryTimestamp",
    "TemporaryUnknownGroupData", "SubCellsTimestamp", "SubCellsUnknown",
    "Persistent", "Temporary", "SubCells", "TopCell", "NavigationMeshes", "Landscape", "LargeReferences"})
_CONTAINER_TYPES = frozenset({"CELL", "WRLD", "Cell", "Worldspace"})

LCTN_REGISTRATION = frozenset({
    "ACPR", "LCPR", "RCPR", "ACUN", "LCUN", "RCUN", "ACSR", "LCSR", "RCSR", "ACEP", "LCEP", "RCEP", "ACID", "LCID",
    "ACEC", "LCEC", "RCEC",
    "PersistentActorReferencesAdded", "PersistentActorReferencesRemoved", "UniqueActorReferencesAdded",
    "UniqueActorReferencesRemoved", "LocationRefTypeReferencesAdded", "EnableParentReferencesAdded",
    "InitiallyDisabledReferencesAdded", "WorldspaceCellsAdded", "WorldspaceCellsRemoved",
    "ActorCellPersistentReferences", "LocationCellPersistentReferences", "ReferenceCellPersistentReferences",
    "ActorCellUniques", "LocationCellUniques", "ReferenceCellUnique", "ActorCellStaticReferences",
    "LocationCellStaticReferences", "ReferenceCellStaticReferences", "ActorCellEncounterCell",
    "LocationCellEncounterCell", "ReferenceCellEncounterCell", "ActorCellMarkerReference",
    "LocationCellMarkerReference", "ActorCellEnablePoint", "LocationCellEnablePoint"})

KIND_OF_FIELD = {"Placement.Position": "MOVE", "Placement.Rotation": "ROTATE", "Base": "BASE",
                 "EnableParent": "ENABLE-PARENT", "LinkedReferences": "LINKED-REF",
                 "LocationReference": "LOCATION-REF", "Owner": "OWNER", "Scale": "SCALE"}


def seg(path) -> list[str]:
    """Path segments with list indices stripped: 'Data[3].Location' -> ['Data', 'Location']."""
    return [p.split("[")[0] for p in str(path).split(".") if p.split("[")[0]]


def pos_equal(a, b) -> bool:
    return all(abs(float(x) - float(y)) <= POS_TOL for x, y in zip(a, b))


def rot_equal(a, b) -> bool:
    def d(x, y):
        t = (float(x) - float(y)) % (2 * math.pi)
        return min(t, 2 * math.pi - t)
    return all(d(x, y) <= ROT_TOL for x, y in zip(a, b))


def bounds_equal(a, b) -> bool:
    return len(a) == len(b) and all(abs(float(x) - float(y)) <= BOUNDS_TOL for x, y in zip(a, b))


def _flags(value) -> int:
    s = str(value)
    return int(s, 16) if s.startswith("0x") else 0


def classify(record_type: str, row_class: str, changed_fields: list[dict], *, canon=None,
             identity: frozenset | set = frozenset()) -> list[dict]:
    """The edits of one patched record against its unpatched winner.

    row_class: 'addition' (one ADD), 'container-bookkeeping' (no edits), else the record's changed fields are grouped.
    changed_fields: [{field, before, after, decoded?: {before, after}, bookkeeping?: bool}], field as the reader names it.
    canon: field name -> its canonical names (first one used for FIELD:<name>); default the name itself.
    identity: field names the caller showed equal to the winner after decoding (identity forwards), dropped.
    Returns [{kind, paths, canonPaths, bookkeepingKind}], in first-seen kind order with DISABLE last.
    """
    names = (lambda n: list(canon.get(n, [n]))) if canon is not None else (lambda n: [n])
    if row_class == "container-bookkeeping":
        return []
    if row_class == "addition":
        return [{"kind": "ADD", "paths": ["<record>"], "canonPaths": ["<record>"], "bookkeepingKind": False}]
    fields = [f for f in changed_fields
              if not f.get("bookkeeping") and f["field"] not in HEADER_BOOKKEEPING and f["field"] not in identity
              and not (record_type in _CONTAINER_TYPES and f["field"].split(".")[0].split("[")[0] in GROUP_BOOKKEEPING)]
    if not fields:
        return []
    groups: dict[str, list] = {}
    bk_kinds: set[str] = set()
    disabled = False
    for f in fields:
        nm = f["field"]
        if nm == "RecordFlags":
            dec = f.get("decoded") or {}
            before, after = str(dec.get("before")), str(dec.get("after"))
            if ("InitiallyDisabled" in after and "InitiallyDisabled" not in before) or \
                    ("Deleted" in after and "Deleted" not in before):
                disabled = True
                continue
            if "InitiallyDisabled" in before and "InitiallyDisabled" not in after:
                groups.setdefault("ENABLE", []).append(f)
                continue
            groups.setdefault("RECORD-FLAGS", []).append(f)
            if (_flags(f.get("before")) ^ _flags(f.get("after"))) == FLAG_PERSISTENT:
                bk_kinds.add("RECORD-FLAGS")  # the Persistent flag set alongside a new reference to it
            continue
        if nm == "EnableParent" and isinstance(f.get("after"), dict) and f["after"].get("parent") == PLAYER \
                and (f["after"].get("flags", 0) & 1):
            disabled = True
            groups.setdefault("_disable_parts", []).append(f)
            continue
        if nm in LCTN_REGISTRATION or any(c in LCTN_REGISTRATION for c in names(nm)):
            kind = "FIELD:" + names(nm)[0]  # one FIELD:<list> per registration list
            groups.setdefault(kind, []).append(f)
            bk_kinds.add(kind)
            continue
        if nm == "MHDT" and record_type == "WRLD":
            kind = "FIELD:MaxHeight"
        else:
            kind = KIND_OF_FIELD.get(nm, "FIELD:" + (seg(names(nm)[0]) or [nm])[0])
        groups.setdefault(kind, []).append(f)
    if disabled:
        parts = groups.pop("_disable_parts", [])
        for k in ("MOVE", "ROTATE", "ENABLE-PARENT"):
            parts += groups.pop(k, [])
        parts += [f for f in fields if f["field"] == "RecordFlags"]
        groups.pop("RECORD-FLAGS", None)
        bk_kinds.discard("RECORD-FLAGS")
        groups["DISABLE"] = parts
    groups.pop("_disable_parts", None)
    out = []
    for kind, fs in groups.items():
        paths = [f["field"] for f in fs]
        out.append({"kind": kind, "paths": paths, "canonPaths": sorted({c for n in paths for c in names(n)}),
                    "bookkeepingKind": kind in bk_kinds})
    return out


def absorb_disabled(edits: list[dict]) -> tuple[list[dict], list[dict]]:
    """DISABLE absorbs every other edit on the same record (it has no effect in game). Each edit carries `formKey`.
    Returns (kept, carried); the carried edits' paths join the DISABLE edit's paths and its `absorbedKinds`."""
    disabled = {e["formKey"]: e for e in edits if e["kind"] == "DISABLE"}
    kept, carried = [], []
    for e in edits:
        d = disabled.get(e["formKey"])
        if d is not None and e is not d:
            carried.append(e)
            d["paths"] = list(d["paths"]) + [p for p in e["paths"] if p not in d["paths"]]
            d["canonPaths"] = list(d.get("canonPaths") or d["paths"]) + \
                [p for p in (e.get("canonPaths") or e["paths"]) if p not in (d.get("canonPaths") or [])]
            d.setdefault("absorbedKinds", []).append(e["kind"])
        else:
            kept.append(e)
    return kept, carried
