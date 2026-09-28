"""Exact v1/v2 candidate joins for viewprobe planes reports."""
import json

SCHEMA_V1 = "occlusion-view-research-v1"
SCHEMA_V2 = "occlusion-view-research-v2"


class PlanesReportError(ValueError):
    pass


class UnsupportedPlanesReportSchema(PlanesReportError):
    pass


def schema_of(doc):
    schema = doc.get("schema") if isinstance(doc, dict) else None
    if schema not in (SCHEMA_V1, SCHEMA_V2):
        raise UnsupportedPlanesReportSchema("unknown planes report schema %r" % schema)
    return schema


def _array(value, what):
    if not isinstance(value, list):
        raise PlanesReportError("%s must be an array" % what)
    return value


def _object(value, what):
    if not isinstance(value, dict):
        raise PlanesReportError("%s must be an object" % what)
    return value


def _id(value, what):
    if type(value) is not int or value < 0:
        raise PlanesReportError("%s must be a nonnegative integer" % what)
    return value


def materialise(doc):
    """Return complete rows for each plane, mirroring C# Materialise."""
    schema = schema_of(doc)
    planes = _array(doc.get("planes"), "planes")
    if schema == SCHEMA_V1:
        return [[dict(_object(c, "v1 candidate")) for c in
                 _array(_object(p, "plane").get("candidates"), "plane candidates")]
                for p in planes]
    bodies = _array(doc.get("candidateBodies"), "candidateBodies")
    for i, body in enumerate(bodies):
        if _id(_object(body, "body").get("id"), "body id") != i:
            raise PlanesReportError("body id at index %d must equal its index" % i)
    result = []
    for p in planes:
        rows = []
        for overlay in _array(_object(p, "plane").get("candidates"), "plane candidates"):
            overlay = _object(overlay, "overlay")
            i = _id(overlay.get("candidate"), "overlay id")
            if i >= len(bodies):
                raise PlanesReportError("overlay references missing body %d" % i)
            row = {k: v for k, v in bodies[i].items() if k != "id"}
            duplicate = row.keys() & (overlay.keys() - {"candidate"})
            if duplicate:
                raise PlanesReportError("body/overlay duplicate key %r" % sorted(duplicate)[0])
            row.update((k, v) for k, v in overlay.items() if k != "candidate")
            rows.append(row)
        result.append(rows)
    return result


def rows_by_plane(doc):
    rows = materialise(doc)
    return zip(doc["planes"], rows)


DEC = json.JSONDecoder()


def _ws(t, i):
    while i < len(t) and t[i] in " \r\n\t":
        i += 1
    return i


def _end(t, i):
    """Validate and skip a JSON value without constructing its containers."""
    i = _ws(t, i)
    if i >= len(t):
        raise PlanesReportError("truncated JSON")
    if t[i] not in "{[":
        try:
            return DEC.raw_decode(t, i)[1]
        except ValueError as exc:
            raise PlanesReportError("malformed JSON value") from exc
    kind = t[i]
    i = _ws(t, i + 1)
    close = "}" if kind == "{" else "]"
    if t[i:i + 1] == close:
        return i + 1
    while True:
        if kind == "{":
            try:
                key, i = DEC.raw_decode(t, i)
            except ValueError as exc:
                raise PlanesReportError("malformed JSON object key") from exc
            if not isinstance(key, str):
                raise PlanesReportError("JSON object key must be a string")
            i = _ws(t, i)
            if t[i:i + 1] != ":":
                raise PlanesReportError("missing JSON colon")
            i = _ws(t, i + 1)
        i = _ws(t, _end(t, i))
        if t[i:i + 1] == close:
            return i + 1
        if t[i:i + 1] != ",":
            raise PlanesReportError("malformed JSON separator")
        i = _ws(t, i + 1)
        if t[i:i + 1] == close:
            raise PlanesReportError("trailing JSON comma")


def _members(t, start):
    i = _ws(t, start)
    if t[i:i + 1] != "{":
        raise PlanesReportError("expected object")
    i = _ws(t, i + 1)
    out = {}
    while t[i:i + 1] != "}":
        try:
            key, i = DEC.raw_decode(t, i)
        except ValueError as exc:
            raise PlanesReportError("malformed object key") from exc
        if not isinstance(key, str) or key in out:
            raise PlanesReportError("duplicate or non-string key %r" % key)
        i = _ws(t, i)
        if t[i:i + 1] != ":":
            raise PlanesReportError("missing colon")
        a = _ws(t, i + 1)
        b = _end(t, a)
        out[key] = (a, b)
        i = _ws(t, b)
        if t[i:i + 1] == ",":
            i = _ws(t, i + 1)
            if t[i:i + 1] == "}":
                raise PlanesReportError("trailing object comma")
        elif t[i:i + 1] != "}":
            raise PlanesReportError("malformed object separator")
    return out


def _elements(t, start):
    i = _ws(t, start)
    if t[i:i + 1] != "[":
        raise PlanesReportError("expected array")
    i = _ws(t, i + 1)
    while t[i:i + 1] != "]":
        a = i
        b = _end(t, a)
        yield a, b
        i = _ws(t, b)
        if t[i:i + 1] == ",":
            i = _ws(t, i + 1)
            if t[i:i + 1] == "]":
                raise PlanesReportError("trailing array comma")
        elif t[i:i + 1] != "]":
            raise PlanesReportError("malformed array separator")


def _decode(t, span):
    return json.loads(t[span[0]:span[1]])


def masked_rows_text(text, keys):
    """Validate all v2 overlays, decoding full bodies only for nonnull masks."""
    start = _ws(text, 0)
    if _ws(text, _end(text, start)) != len(text):
        raise PlanesReportError("trailing JSON content")
    root = _members(text, start)
    schema = _decode(text, root["schema"]) if "schema" in root else None
    schema_of({"schema": schema})
    if "planes" not in root:
        raise PlanesReportError("planes array missing")
    if schema == SCHEMA_V1:
        rows, seen, skipped = [], 0, 0
        for pa, _ in _elements(text, root["planes"][0]):
            plane = _members(text, pa)
            if "candidates" not in plane:
                raise PlanesReportError("plane candidates missing")
            for span in _elements(text, plane["candidates"][0]):
                seen += 1
                fields = _members(text, span[0])
                if "visibilityMask" not in fields or _decode(text, fields["visibilityMask"]) is None:
                    skipped += 1
                    continue
                obj = _decode(text, span)
                rows.append({k: obj.get(k) for k in keys})
        return rows, {"rowsSeen": seen, "rowsSkippedNoMask": skipped,
                      "rowsDecoded": len(rows)}
    if "candidateBodies" not in root:
        raise PlanesReportError("v2 candidateBodies missing")
    bodies = []
    for i, span in enumerate(_elements(text, root["candidateBodies"][0])):
        fields = _members(text, span[0])
        if "id" not in fields or _id(_decode(text, fields["id"]), "body id") != i:
            raise PlanesReportError("body id at index %d must equal its index" % i)
        bodies.append((span, set(fields) - {"id"}, fields.get("visibilityMask")))
    rows, seen, skipped = [], 0, 0
    for pa, _ in _elements(text, root["planes"][0]):
        plane = _members(text, pa)
        if "candidates" not in plane:
            raise PlanesReportError("plane candidates missing")
        for oa, ob in _elements(text, plane["candidates"][0]):
            seen += 1
            fields = _members(text, oa)
            if "candidate" not in fields:
                raise PlanesReportError("overlay id missing")
            i = _id(_decode(text, fields["candidate"]), "overlay id")
            if i >= len(bodies):
                raise PlanesReportError("overlay references missing body %d" % i)
            duplicate = bodies[i][1] & (set(fields) - {"candidate"})
            if duplicate:
                raise PlanesReportError("body/overlay duplicate key %r" % sorted(duplicate)[0])
            mask_span = fields.get("visibilityMask", bodies[i][2])
            if mask_span is None or _decode(text, mask_span) is None:
                skipped += 1
                continue
            body = _decode(text, bodies[i][0])
            overlay = _decode(text, (oa, ob))
            row = {k: v for k, v in body.items() if k != "id"}
            row.update((k, v) for k, v in overlay.items() if k != "candidate")
            rows.append({k: row.get(k) for k in keys})
    return rows, {"rowsSeen": seen, "rowsSkippedNoMask": skipped,
                  "rowsDecoded": len(rows)}
