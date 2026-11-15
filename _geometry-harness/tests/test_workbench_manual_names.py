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


def manual_bundle(root: Path, arms, review=None) -> Path:
    """A bundle whose plan has `arms` [(armId, kind, [plugins])], target Sel.esp, and one overlap row naming a reference
    Sel.esp defines; `review` is the plan's reviewArm (absent when None)."""
    d = root / "bundle"
    (d / "emits").mkdir(parents=True)
    files = root / "files"
    files.mkdir()
    names = sorted({p for _, _, ps in arms for p in ps})
    for n in names:
        header_only_plugin(files / n, masters=() if n == "Base.esm" else ("Base.esm",))
    row = lambda n: {"name": n, "path": str(files / n), "sha256": sha(files / n)}
    plan = {"planId": "manual-names", "targets": [{"name": "Sel.esp"}],
            "arms": [{"armId": a, "kind": k, "candidateFile": f"{a}.json", "plugins": ps,
                      "requested": {"plugins": [row(n) for n in ps]}} for a, k, ps in arms]}
    if review:
        plan["reviewArm"] = review
    (d / "plan.json").write_text(json.dumps(plan), encoding="utf-8")
    emit = {"Conflicts": [{"Kind": "OVERLAP", "A": "Sel.esp", "B": "Base.esm", "Summary": "s",
                           "Necessary": [{"FormKey": "000801:Sel.esp"}], "Alternative": [{"FormKey": "000900:Base.esm"}]}]}
    (d / "emits" / "overlap.json").write_text(json.dumps(emit), encoding="utf-8")
    (d / "disposition.json").write_text("{}", encoding="utf-8")
    (d / "manifest.json").write_text(json.dumps({"evidence": [{"kind": "emit", "path": "emits/overlap.json",
                                                                "checkId": "geometry.object-overlap"}]}), encoding="utf-8")
    return d


def names_read(arms, review=None):
    with tempfile.TemporaryDirectory(prefix="wb-manual-names-") as t:
        root = Path(t)
        d = manual_bundle(root, arms, review)
        stub = root / "stub_reader.py"
        stub.write_text(STUB, encoding="utf-8")
        bundleenrich.enrich_names(d, f'"{sys.executable}" "{stub}"', {})
        names = json.loads((d / "enrichment" / "names" / "names.json").read_text(encoding="utf-8"))
        print(f"   review={review}: readOrder {names['readOrder']}; unread objects {names['unread']['objects']}")
        return names


MANUAL = [("baseline", "baseline", ["Base.esm"]), ("candidate-01", "candidate", ["Base.esm", "Sel.esp"])]


def test_the_review_arms_plugins_are_read():
    """A manual plan: the baseline is the profile without the selection, the review arm the profile as it is."""
    names = names_read(MANUAL, review="candidate-01")
    assert "Sel.esp" in names["readOrder"], "the names read skipped the review arm's selected plugin"
    assert "000801:Sel.esp" not in names["unread"]["objects"], "a reference the selected plugin defines was left unread"


def test_a_plan_without_a_review_arm_reads_the_baseline_as_today():
    """A pipeline plan: no reviewArm, the baseline is the combined arm; the read order is its plugins."""
    arms = [("baseline", "baseline", ["Base.esm", "Sel.esp"]), ("candidate-01", "candidate", ["Base.esm"])]
    names = names_read(arms)
    assert names["readOrder"] == ["Base.esm", "Sel.esp"], names["readOrder"]


def test_the_review_arm_is_read_even_when_another_arm_loads_more_plugins():
    """Rejects a 'largest arm' heuristic: the largest arm lacks the selection; only the review arm's list reads it."""
    arms = MANUAL + [("candidate-02", "candidate", ["Base.esm", "Big1.esp", "Big2.esp"])]
    names = names_read(arms, review="candidate-01")
    assert "000801:Sel.esp" not in names["unread"]["objects"], "the review arm's selected plugin was not read"


def test_a_review_arm_that_names_no_arm_is_refused_not_read_as_the_baseline():
    """Review L6: a plan whose reviewArm names no arm is a broken plan; the names read refuses it in one sentence."""
    try:
        names_read(MANUAL, review="candidate-09")
    except bundleenrich.Refused as ex:
        assert "candidate-09" in str(ex), ex
        return
    raise AssertionError("a reviewArm that names no arm was read anyway")


def test_the_union_of_all_arms_is_not_what_is_read():
    """Review L6: with a review arm the read order is that arm's plugins (plus the patches rule), not every arm's."""
    arms = MANUAL + [("candidate-02", "candidate", ["Base.esm", "Sel.esp", "Extra.esp"])]
    names = names_read(arms, review="candidate-01")
    # Extra.esp is loaded by another arm and is not a target: today's patches rule names it; a union would order it
    # before the review arm's own plugins
    assert names["readOrder"][:2] == ["Base.esm", "Sel.esp"], names["readOrder"]


if __name__ == "__main__":
    failed = 0
    for name, fn in [(k, v) for k, v in list(globals().items()) if k.startswith("test_")]:
        try:
            fn()
            print("PASS", name)
        except AssertionError as ex:
            failed += 1
            print("FAIL", name, "-", ex)
    sys.exit(1 if failed else 0)
