"""Known answer: in a manual-mode bundle, the names read reaches the selected mod's own references.

Manual mode compares the profile without the selected mods (evalrun's baseline arm) with the profile as it is (the
candidate arm), and the workbench opens the candidate arm. A row there that names a reference the selected plugin
defines must have that reference read, or the Conflicts tab cannot give it a cell and the viewer cannot open it.

Wrong answer rejected: a names read that takes its plugin list from the baseline arm only, so a plugin only the
candidate arm loads (the selection) is never read and its references are listed unread.

    python -I _geometry-harness/tests/test_workbench_manual_names.py      # exit 0 pass, 1 fail
"""
import hashlib
import json
import struct
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import bundleenrich  # noqa: E402

STUB = r'''
import json, sys, os
for line in sys.stdin:
    m = json.loads(line)
    if "id" not in m:
        continue
    res = {}
    if m.get("method") == "tools/call":
        a = m["params"]["arguments"]
        plugin = os.path.basename(a["source"]).lower()
        recs = []
        for fk in a["formids"]:
            if fk.split(":", 1)[1].lower() == plugin:
                fields = [{"path": "Base", "value": "000ABC:Base.esm"}] if not fk.upper().startswith("000ABC") else \
                         [{"path": "Name", "value": "a base"}, {"path": "Model.File", "value": "x.nif"}]
                recs.append({"formid": fk, "type": "PlacedObject" if fields[0]["path"] == "Base" else "Static",
                             "editorid": "Ed" + fk[:6], "fields": fields})
            else:
                recs.append({"formid": fk, "error": "not in this plugin"})
        res = {"content": [{"type": "text", "text": json.dumps({"ok": True, "source": a["source"] + " (file, not active)",
                                                              "records": recs})}]}
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": m["id"], "result": res}) + "\n")
    sys.stdout.flush()
'''


def header_only_plugin(p: Path, masters=()):
    """A TES4 header and nothing else (bundleenrich.private_instance's shape), with optional masters."""
    hedr = b"HEDR" + struct.pack("<H", 12) + struct.pack("<fiI", 1.71, 0, 0x800)
    mast = b"".join(b"MAST" + struct.pack("<H", len(m) + 1) + m.encode() + b"\0" +
                    b"DATA" + struct.pack("<H", 8) + b"\0" * 8 for m in masters)
    body = hedr + mast
    p.write_bytes(b"TES4" + struct.pack("<IIIIHH", len(body), 0, 0, 0, 44, 0) + body)


def sha(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def manual_bundle(root: Path) -> Path:
    d = root / "bundle"
    (d / "emits").mkdir(parents=True)
    files = root / "files"
    files.mkdir()
    header_only_plugin(files / "Base.esm")
    header_only_plugin(files / "Sel.esp", masters=("Base.esm",))
    row = lambda n: {"name": n, "path": str(files / n), "sha256": sha(files / n)}
    plan = {"planId": "manual-names", "targets": [{"name": "Sel.esp"}],
            "arms": [{"armId": "baseline", "kind": "baseline", "candidateFile": "candidate-00-baseline.json",
                      "plugins": ["Base.esm"], "requested": {"plugins": [row("Base.esm")]}},
                     {"armId": "candidate-01", "kind": "candidate", "candidateFile": "candidate-01.json",
                      "plugins": ["Base.esm", "Sel.esp"], "requested": {"plugins": [row("Base.esm"), row("Sel.esp")]}}]}
    (d / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
    emit = {"Conflicts": [{"Kind": "OVERLAP", "A": "Sel.esp", "B": "Base.esm", "Summary": "s",
                           "Necessary": [{"FormKey": "000801:Sel.esp"}], "Alternative": [{"FormKey": "000900:Base.esm"}]}]}
    (d / "emits" / "overlap-candidate-01.json").write_text(json.dumps(emit), encoding="utf-8")
    (d / "disposition.json").write_text("{}", encoding="utf-8")
    (d / "manifest.json").write_text(json.dumps({"evidence": [{"kind": "emit", "path": "emits/overlap-candidate-01.json",
                                                                "checkId": "geometry.object-overlap",
                                                                "armId": "candidate-01"}]}), encoding="utf-8")
    return d


def test_selection_references_are_read():
    with tempfile.TemporaryDirectory(prefix="wb-manual-names-") as t:
        root = Path(t)
        d = manual_bundle(root)
        stub = root / "stub_reader.py"
        stub.write_text(STUB, encoding="utf-8")
        bundleenrich.enrich_names(d, f'"{sys.executable}" "{stub}"', {})
        names = json.loads((d / "enrichment" / "names" / "names.json").read_text(encoding="utf-8"))
        unread = names["unread"]["objects"]
        print(f"readOrder {names['readOrder']}; unread objects {unread}")
        assert "Sel.esp" in names["readOrder"], "the names read skipped the selected plugin (only the candidate arm loads it)"
        assert "000801:Sel.esp" not in unread, "a reference the selected plugin defines was left unread"


if __name__ == "__main__":
    try:
        test_selection_references_are_read()
    except AssertionError as ex:
        print("FAIL:", ex)
        sys.exit(1)
    print("PASS")
