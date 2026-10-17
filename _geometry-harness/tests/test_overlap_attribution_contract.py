"""Independent Q47 acceptance controls, frozen before implementation inspection."""
from __future__ import annotations
import copy, importlib.util, json, unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE = ROOT / "_geometry-harness" / "overlap_attribution.py"

def _module():
    spec = importlib.util.spec_from_file_location("overlap_attribution", MODULE)
    assert MODULE.exists() and spec and spec.loader
    mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
    assert callable(getattr(mod, "attribute", None)); return mod

def _record(key, asset, **fields):
    base = {"effectiveBase":{"formid":"000010:Skyrim.esm"}, "recordBase":{"formid":"000010:Skyrim.esm"},
            "position":[0,0,0], "rotation":[0,0,0], "scale":1.0, "deletion":False,
            "resolvedEnable":{"resolved":True,"enabled":True}, "bos":None,
            "cell":{"formid":"000020:Skyrim.esm"}, "space":None}
    base.update(fields)
    pos=base["position"]
    return {"formid":key,"type":"PlacedObject","editorid":key.split(":")[0],"name":None,
            "winner":"Combined.esp","fields":base,
            "geometry":{"assetId":asset,"status":"measured",
                        "worldBounds":{"min":[pos[0]-10,pos[1]-10,pos[2]-10],
                                       "max":[pos[0]+10,pos[1]+10,pos[2]+10]}},
            "eligibility":{"status":"eligible","reason":None}}

def _pair(a,b):
    return {"pairKey":"||".join(sorted((a,b))),"a":a,"b":b,"historicalRowIds":[],
            "conflictIoRowIdentity":None,"actualShape":{"status":"measured","reason":None,
            "surfaceRelation":"intersects","intersects":True,"trianglePairsTested":10}}

def _setup(sid, records, pairs, *, complete=True, hashes=None, limits=None):
    hashes=hashes or {}; assets=[]
    for r in records:
        aid=r["geometry"]["assetId"]
        assets.append({"assetId":aid,"consumedSha256":hashes.get(aid,"sha256:"+aid),
                       "winner":{"provider":"fixture-assets"}})
    return {"job":"fixture "+sid,"readerVersion":"reader-v1","policyVersion":"predicate-v1",
            "scope":{"predicate":{"policyVersion":"predicate-v1","tolerance":1e-5,"maxTrianglePairs":2000000,"pairSeconds":5},
            "configuration":{"Fingerprint":"fp-"+sid,
            "Providers":[{"Name":"provider:"+sid}],"Limits":limits or []}},
            "total":len(pairs),"complete":complete,"notes":[],"records":records,"assets":assets,
            "pairs":pairs,"scene":{"role":"combined","validity":{"status":"valid" if complete else "unresolved",
            "reasons":[] if complete else ["fixture incomplete"]},
            "discovery":{"scope":"fixture","complete":complete}}}

def _attribute(setups, comparisons):
    return _module().attribute(copy.deepcopy(setups),"combined",comparisons,["FixtureA.esp","FixtureB.esp"])

class AttributionContractTests(unittest.TestCase):
    def test_both_objects_and_complete_asset_view(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        source=_setup("source",[_record(a,"a"),_record(b,"b")],[p],hashes={"a":"old","b":"same"})
        combined=_setup("combined",[_record(a,"a"),_record(b,"b")],[p],hashes={"a":"new","b":"same"})
        row=_attribute({"source":source,"combined":combined},["source"])["pairs"][0]
        self.assertEqual("changed_existing",row["attribution"]["status"])
        changed=row["attribution"]["comparisons"][0]["changedFields"]
        self.assertIn("model",changed[a]); self.assertEqual([],changed[b])

    def test_setup_and_pair_order_independent(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        s=_setup("source",[_record(a,"a"),_record(b,"b")],[p])
        c=_setup("combined",[_record(a,"a"),_record(b,"b")],[p])
        rev=copy.deepcopy(p); rev["a"],rev["b"]=rev["b"],rev["a"]
        sr=_setup("source",[_record(b,"b"),_record(a,"a")],[rev])
        cr=_setup("combined",[_record(b,"b"),_record(a,"a")],[rev])
        o1=_attribute({"source":s,"combined":c},["source"])
        o2=_attribute({"combined":cr,"source":sr},["source"])
        self.assertEqual(o1["pairs"][0]["attribution"]["status"],o2["pairs"][0]["attribution"]["status"])
        self.assertEqual(o1["pairs"][0]["attribution"]["oldNarrowVerdict"],o2["pairs"][0]["attribution"]["oldNarrowVerdict"])
        self.assertEqual(o1["accounting"],o2["accounting"])

    def test_multi_comparison_precedence_is_order_invariant(self):
        a,b="000001:A.esp","000002:B.esp"; p=_pair(a,b)
        combined=_setup("combined",[_record(a,"a"),_record(b,"b")],[p])
        inherited=_setup("inherited",[_record(a,"a"),_record(b,"b")],[p])
        changed=_setup("changed",[_record(a,"a",position=[12,0,0]),_record(b,"b")],[p])
        absent=_setup("absent",[_record(a,"a",deletion=True),_record(b,"b")],[p])
        unknown=copy.deepcopy(inherited); unknown["readerVersion"]="reader-v2"
        setups={"combined":combined,"inherited":inherited,"changed":changed,"absent":absent,"unknown":unknown}
        for names,want in ((["absent","inherited"],"new"),
                           (["changed","inherited"],"changed_existing"),
                           (["unknown","absent","changed","inherited"],"unresolved")):
            forward=_attribute(setups,names); reverse=_attribute(setups,list(reversed(names)))
            self.assertEqual(want,forward["pairs"][0]["attribution"]["status"])
            self.assertEqual(forward["pairs"][0]["attribution"],reverse["pairs"][0]["attribution"])
            self.assertEqual(forward["accounting"],reverse["accounting"])
        mod=_module()
        self.assertEqual("absent_excluded",mod._aggregate_status(["inherited","absent_excluded"]))
        self.assertEqual("new",mod._aggregate_status(["absent_excluded","new"]))

    def test_missing_source_endpoint_is_new_but_combined_missing_stays_unresolved(self):
        a,b="000001:A.esp","000002:B.esp"; p=_pair(a,b)
        source=_setup("source",[_record(a,"a")],[])
        combined=_setup("combined",[_record(a,"a"),_record(b,"b")],[p])
        self.assertEqual("new",_attribute({"source":source,"combined":combined},["source"])["pairs"][0]["attribution"]["status"])
        source_full=_setup("source",[_record(a,"a"),_record(b,"b")],[p])
        combined_missing=_setup("combined",[_record(a,"a")],[])
        self.assertEqual("unresolved",_attribute({"source":source_full,"combined":combined_missing},["source"])["pairs"][0]["attribution"]["status"])

    def test_transform_enable_deletion_and_bos_changes_reviewable(self):
        cases=(("rotation",[0,0,90]),("scale",1.25),
               ("resolvedEnable",{"resolved":True,"enabled":False}),
               ("deletion",True),("bos",{"swap":"door_closed.nif"}))
        for field,value in cases:
            with self.subTest(field=field):
                a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
                source=_setup("source",[_record(a,"a"),_record(b,"b")],[p])
                combined=_setup("combined",[_record(a,"a",**{field:value}),_record(b,"b")],[p])
                row=_attribute({"source":source,"combined":combined},["source"])["pairs"][0]
                self.assertIn(row["attribution"]["status"],{"changed_existing","absent_excluded"})
                self.assertNotIn("repaired",json.dumps(row).lower())

    def test_asset_only_same_path_changed_bytes(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        source=_setup("source",[_record(a,"same.nif"),_record(b,"b")],[p],hashes={"same.nif":"old"})
        combined=_setup("combined",[_record(a,"same.nif"),_record(b,"b")],[p],hashes={"same.nif":"new"})
        row=_attribute({"source":source,"combined":combined},["source"])["pairs"][0]
        self.assertEqual("changed_existing",row["attribution"]["status"])

    def test_changed_doorway_no_repair_credit(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        source=_setup("source",[_record(a,"door.nif"),_record(b,"frame.nif")],[p],hashes={"door.nif":"v1"})
        combined=_setup("combined",[_record(a,"door.nif"),_record(b,"frame.nif")],[p],hashes={"door.nif":"v2"})
        row=_attribute({"source":source,"combined":combined},["source"])["pairs"][0]
        self.assertEqual("changed_existing",row["attribution"]["status"])
        self.assertNotIn("repaired",json.dumps(row).lower())

    def test_unknown_budget_reason_survives(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        p["actualShape"]={"status":"unmeasurable","intersects":None,"reason":"triangle budget exhausted"}
        c=_setup("combined",[_record(a,"a"),_record(b,"b")],[p],complete=False,limits=["triangle budget exhausted"])
        s=copy.deepcopy(c); s["scope"]["configuration"]["Fingerprint"]="fp-source"
        out=_attribute({"source":s,"combined":c},["source"]); row=out["pairs"][0]
        self.assertEqual("unresolved",row["attribution"]["status"])
        self.assertIsNone(row["attribution"]["comparisons"][0]["after"]["relation"]["intersects"])
        self.assertIn("triangle budget exhausted",out["setups"]["combined"]["limits"])
        self.assertFalse(out["accounting"]["attribution"]["complete"])

    def test_non_effective_provenance_and_work_counts_do_not_create_change(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; old=_pair(a,b); new=copy.deepcopy(old)
        new["actualShape"]["trianglePairsTested"]=999
        sr,cr=_record(a,"a"),_record(a,"a"); cr["winner"]="ForwardOnly.esp"
        s=_setup("source",[sr,_record(b,"b")],[old]); c=_setup("combined",[cr,_record(b,"b")],[new])
        row=_attribute({"source":s,"combined":c},["source"])["pairs"][0]
        self.assertEqual("inherited",row["attribution"]["status"])

    def test_reader_or_predicate_mismatch_is_unresolved(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        s=_setup("source",[_record(a,"a"),_record(b,"b")],[p]); c=copy.deepcopy(s)
        s["readerVersion"]="reader-v1"; c["readerVersion"]="reader-v2"
        s["policyVersion"]="predicate-v1"; c["policyVersion"]="predicate-v2"
        c["scope"]["configuration"]["Fingerprint"]="fp-combined"
        row=_attribute({"source":s,"combined":c},["source"])["pairs"][0]
        self.assertEqual("unresolved",row["attribution"]["status"])

    def test_measured_narrow_without_origin_is_explicitly_unavailable(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        p["oldNarrowVerdict"]={"status":"measured","origin":None,"reason":"geometry unavailable"}
        row=_attribute({"source":_setup("source",[_record(a,"a"),_record(b,"b")],[p]),
                        "combined":_setup("combined",[_record(a,"a"),_record(b,"b")],[p])},["source"])["pairs"][0]
        diagnostic=row["attribution"]["oldNarrowVerdict"]
        self.assertEqual("unavailable",diagnostic["status"])
        self.assertIsNone(diagnostic["agreement"])
        self.assertIn("no resolved Q46 narrow counterfactual",diagnostic["reason"])

    def test_resolved_narrow_with_unresolved_final_has_unknown_agreement(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        p["oldNarrowVerdict"]={"status":"measured","origin":"introduced"}
        source=_setup("source",[_record(a,"a"),_record(b,"b")],[p])
        combined=_setup("combined",[_record(a,"a"),_record(b,"b")],[p])
        combined["readerVersion"]="reader-v2"
        row=_attribute({"source":source,"combined":combined},["source"])["pairs"][0]
        diagnostic=row["attribution"]["oldNarrowVerdict"]
        self.assertEqual("unresolved",row["attribution"]["status"])
        self.assertEqual("unresolved",diagnostic["status"])
        self.assertIsNone(diagnostic["agreement"])
        self.assertIn("cannot be evaluated",diagnostic["reason"])

    def test_narrow_diagnostic_does_not_change_attribution_or_accounting(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        source=_setup("source",[_record(a,"a"),_record(b,"b")],[p])
        combined=_setup("combined",[_record(a,"a"),_record(b,"b")],[p])
        unavailable=_attribute({"source":source,"combined":combined},["source"])
        resolved_pair=copy.deepcopy(p)
        resolved_pair["oldNarrowVerdict"]={"status":"measured","origin":"inherited"}
        resolved=_attribute({"source":_setup("source",[_record(a,"a"),_record(b,"b")],[resolved_pair]),
                             "combined":_setup("combined",[_record(a,"a"),_record(b,"b")],[resolved_pair])},["source"])
        self.assertEqual(unavailable["pairs"][0]["attribution"]["status"],resolved["pairs"][0]["attribution"]["status"])
        self.assertEqual(unavailable["pairs"][0]["attribution"]["comparisons"],resolved["pairs"][0]["attribution"]["comparisons"])
        self.assertEqual(unavailable["accounting"],resolved["accounting"])

    def test_source_only_pair_does_not_copy_measured_relation_into_combined(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        source=_setup("source",[_record(a,"a"),_record(b,"b")],[p])
        combined=_setup("combined",[],[])
        row=_attribute({"source":source,"combined":combined},["source"])["pairs"][0]
        self.assertEqual("unresolved",row["attribution"]["status"])
        self.assertNotEqual(True,(row.get("actualShape") or {}).get("intersects"))

    def test_serialization_and_accounting_preserve_pairs(self):
        ids=("000001:FixtureA.esp","000002:FixtureB.esp","000003:FixtureA.esp","000004:FixtureB.esp")
        pairs=[_pair(ids[0],ids[1]),_pair(ids[2],ids[3])]
        records=[_record(ids[0],"a"),_record(ids[1],"b"),_record(ids[2],"c"),_record(ids[3],"d")]
        out=_attribute({"source":_setup("source",records,pairs),"combined":_setup("combined",records,pairs)},["source"])
        self.assertEqual(sorted(p["pairKey"] for p in pairs),[p["pairKey"] for p in out["pairs"]])
        self.assertEqual(2,out["accounting"]["attribution"]["pairCount"])
        self.assertEqual(2,sum(out["accounting"]["attribution"]["attributionCounts"].values()))
        self.assertEqual(out,json.loads(json.dumps(out,sort_keys=True)))

    def test_downstream_grouping_review_and_unknown_accounting(self):
        a,b,c="000001:FixtureA.esp","000002:FixtureB.esp","000003:FixtureB.esp"
        pairs=[_pair(a,b),_pair(a,c)]
        records=[_record(a,"a",position=[0,0,0]),_record(b,"b",position=[10,0,0]),
                 _record(c,"c",position=[20,0,0])]
        attributed=_attribute({"source":_setup("source",records,pairs),
                               "combined":_setup("combined",records,pairs)},["source"])
        spec=importlib.util.spec_from_file_location("overlap_triage",ROOT/"_geometry-harness"/"overlap_triage.py")
        assert spec and spec.loader; triage=importlib.util.module_from_spec(spec); spec.loader.exec_module(triage)
        policy={"schemaVersion":1,"maxAnalogueExamplesPerPair":3,"incidentRadius":384,
                "pose":{"positionScale":32,"rotationScaleRadians":0.1,"scaleRelativeTolerance":0.05},
                "ranking":{"attribution":{"inherited":1,"new":5,"changed_existing":4,
                "absent_excluded":0,"unresolved":0},"actualIntersection":2,
                "functionalPriority":{},"exactMeshAnalogueAdjustment":-1,"exactBaseAnalogueAdjustment":-1}}
        out=triage.annotate(attributed,policy)
        self.assertEqual(2,out["accounting"]["triage"]["pairsTotal"])
        self.assertEqual(1,out["accounting"]["triage"]["incidentsTotal"])
        self.assertEqual(2,out["accounting"]["triage"]["unreviewedPairs"])
        self.assertEqual(attributed["accounting"]["measurement"],out["accounting"]["measurement"])
        self.assertEqual(attributed["accounting"]["attribution"],out["accounting"]["attribution"])
        self.assertEqual(sorted(p["pairKey"] for p in pairs),sorted(out["incidents"][0]["pairKeys"]))
        for pair in out["pairs"]:
            self.assertEqual({"status":"unreviewed","consequence":None},pair["review"])
            self.assertIsNotNone(pair["incidentId"])
        self.assertNotIn("repaired",json.dumps(out).lower())

    def test_downstream_uses_effective_base_type_and_refuses_bare_extent(self):
        a,b="000001:FixtureA.esp","000002:FixtureB.esp"; p=_pair(a,b)
        p["actualShape"]["intersectionExtent"]=12.5
        ra,rb=_record(a,"a"),_record(b,"b")
        ra["fields"]["effectiveBase"]["type"]="Door"
        attributed=_attribute({"source":_setup("source",[ra,rb],[p]),
                               "combined":_setup("combined",[ra,rb],[p])},["source"])
        spec=importlib.util.spec_from_file_location("overlap_triage",ROOT/"_geometry-harness"/"overlap_triage.py")
        assert spec and spec.loader; triage=importlib.util.module_from_spec(spec); spec.loader.exec_module(triage)
        policy={"schemaVersion":1,"maxAnalogueExamplesPerPair":0,"incidentRadius":384,
                "pose":{"positionScale":32,"rotationScaleRadians":0.1,"scaleRelativeTolerance":0.05},
                "ranking":{"attribution":{"inherited":0},"actualIntersection":0,
                "functionalPriority":{"Door":9},"exactMeshAnalogueAdjustment":0,"exactBaseAnalogueAdjustment":0}}
        row=triage.annotate(attributed,policy)["pairs"][0]
        self.assertIn("Door",[x["type"] for x in row["features"]["functionalContext"]["value"]])
        self.assertNotEqual("measured",row["features"]["actualIntersectionExtent"]["status"])


class RetainedPairAbsenceBoundaryTests(unittest.TestCase):
    """Where the absence boundary sits once a scope import RETAINS a pair row.

    Before scope import, a pair whose member vanished from the combined scene had no row at all,
    so `after_missing` forced `unresolved` (covered by
    test_missing_source_endpoint_is_new_but_combined_missing_stays_unresolved). Importing a scope
    deliberately retains that row, which moves the decision onto the member snapshot -- so these
    cases pin what the retained row is now allowed to conclude.
    """

    def _retained(self, a, b, reason):
        """A retained pair row whose relation could not be measured, exactly as overlapprobe
        emits it when a member is ineligible: unknown, no verdict, member's own reason."""
        row = copy.deepcopy(_pair(a, b))
        row["actualShape"] = {"status": "unknown", "reason": reason, "surfaceRelation": "unknown",
                              "containment": "unknown", "intersects": None,
                              "trianglePairsTested": 0}
        return row

    def _disabled(self, key, asset):
        rec = _record(key, asset, resolvedEnable={"resolved": True, "enabled": False})
        rec["eligibility"] = {"status": "state_excluded",
                              "reason": "the reference is absent in the selected state"}
        return rec

    def test_retained_pair_with_enumerated_disabled_member_is_earned_absence(self):
        """The member IS enumerated and its own resolved state says it is off. That is absence
        established by measurement, which is what absent_excluded is for."""
        a, b = "000001:A.esp", "000002:B.esp"
        row = self._retained(a, b, "the reference is absent in the selected state")
        source = _setup("source", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        combined = _setup("combined", [_record(a, "a"), self._disabled(b, "b")], [row])
        out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
        self.assertEqual("absent_excluded", out["attribution"]["status"])
        after = {o["formid"]: o for o in out["attribution"]["comparisons"][0]["after"]["objects"]}
        self.assertEqual("absent", after[b]["status"])
        self.assertFalse(after[b]["state"]["resolvedEnable"]["enabled"])
        self.assertTrue(after[b]["state"]["resolvedEnable"]["resolved"])

    def test_retained_pair_never_reports_a_measured_relation_for_an_absent_member(self):
        """Whatever the status, a member that is gone must not yield a geometric verdict.
        'separated' is a measurement, and there is nothing to measure against a reference that is
        not there."""
        a, b = "000001:A.esp", "000002:B.esp"
        row = self._retained(a, b, "the reference is absent in the selected state")
        source = _setup("source", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        combined = _setup("combined", [_record(a, "a"), self._disabled(b, "b")], [row])
        out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
        relation = out["attribution"]["comparisons"][0]["after"]["relation"]
        self.assertIsNone(relation["intersects"])
        self.assertNotEqual("separated", relation["surfaceRelation"])
        self.assertTrue(relation["reason"])

    def _imported(self, setup, *, pair_count=1, missing=()):
        """A scene whose scope was IMPORTED, with the producer's own accounting receipt.

        missing=() is the case the producer-side invariant does not cover: a reference with
        history but no winning geometry pose is skipped by the record loop (Program.cs:961) and
        is NOT listed in missingImportedReferences, which filters on missing history
        (Program.cs:1276). It is enumerated nowhere and declared missing nowhere.
        """
        scene = copy.deepcopy(setup)
        scene["scene"]["discovery"] = {
            "scope": "fixture", "complete": True, "method": "fixture-import",
            "importedScopes": [{"path": "scope.json", "sha256": "sha256:scope",
                                "referenceCount": 2, "pairCount": pair_count}],
            "missingImportedReferences": list(missing)}
        return scene

    def test_retained_pair_with_unenumerated_member_is_unresolved(self):
        """A retained pair naming an endpoint the scene never enumerated proves nothing about
        that endpoint. Non-enumeration is not evidence of absence, so the conservative outcome
        is unresolved -- the same floor an unretained pair gets."""
        a, b = "000001:A.esp", "000002:B.esp"
        source = _setup("source", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        combined = _setup("combined", [_record(a, "a")],
                          [self._retained(a, b, "a historical reference is absent from the evaluated order")])
        full = _attribute({"source": source, "combined": combined}, ["source"])
        out = full["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])
        self.assertEqual("a retained pair row names an endpoint this scene never enumerated, "
                         "so its absence is unproven",
                         out["attribution"]["comparisons"][0]["reason"])
        after = {o["formid"]: o for o in out["attribution"]["comparisons"][0]["after"]["objects"]}
        self.assertNotEqual("absent", after[b]["status"])
        self.assertEqual({"unresolved": 1, "new": 0, "inherited": 0, "changed_existing": 0,
                          "absent_excluded": 0},
                         full["accounting"]["attribution"]["attributionCounts"])
        self.assertFalse(full["accounting"]["attribution"]["complete"])

    def test_the_two_absence_situations_are_now_distinguishable(self):
        """The defect this replaced: enumerated-and-disabled and no-record-row produced the same
        absent_excluded from the same 'absent' snapshot. They must now differ at BOTH layers."""
        a, b = "000001:A.esp", "000002:B.esp"
        source = _setup("source", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        row = self._retained(a, b, "the reference is absent in the selected state")
        earned = _attribute({"source": source,
                             "combined": _setup("combined", [_record(a, "a"), self._disabled(b, "b")], [row])},
                            ["source"])["pairs"][0]
        unproven = _attribute({"source": source,
                               "combined": _setup("combined", [_record(a, "a")], [copy.deepcopy(row)])},
                              ["source"])["pairs"][0]
        self.assertEqual("absent_excluded", earned["attribution"]["status"])
        self.assertEqual("unresolved", unproven["attribution"]["status"])
        snapshot = lambda r: {o["formid"]: o["status"]
                              for o in r["attribution"]["comparisons"][0]["after"]["objects"]}
        self.assertNotEqual(snapshot(earned), snapshot(unproven))

    def test_absence_is_decided_by_structured_fields_not_by_the_reason_string(self):
        """Reason strings are for humans and get reworded. Swapping them between the two cases
        must not move either status: the earned case keeps absent_excluded wearing the unproven
        case's prose, and the unproven case stays unresolved wearing the earned case's prose."""
        a, b = "000001:A.esp", "000002:B.esp"
        source = _setup("source", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        for prose in ("the reference is absent in the selected state",
                      "a historical reference is absent from the evaluated order",
                      "", "totally reworded next release"):
            with self.subTest(prose=prose):
                enumerated = _setup("combined", [_record(a, "a"), self._disabled(b, "b")],
                                    [self._retained(a, b, prose)])
                unenumerated = _setup("combined", [_record(a, "a")], [self._retained(a, b, prose)])
                self.assertEqual("absent_excluded",
                    _attribute({"source": source, "combined": enumerated}, ["source"])["pairs"][0]["attribution"]["status"])
                self.assertEqual("unresolved",
                    _attribute({"source": source, "combined": unenumerated}, ["source"])["pairs"][0]["attribution"]["status"])

    def test_before_side_unenumerated_endpoint_of_a_retained_pair_is_unresolved_not_new(self):
        """The mirror. An unproven endpoint on the BEFORE side must not buy an unearned `new` any
        more than one on the after side buys an unearned absent_excluded."""
        a, b = "000001:A.esp", "000002:B.esp"
        source = _setup("source", [_record(a, "a")],
                        [self._retained(a, b, "a historical reference is absent from the evaluated order")])
        combined = _setup("combined", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])
        self.assertEqual("a retained pair row names an endpoint this scene never enumerated, "
                         "so its absence is unproven",
                         out["attribution"]["comparisons"][0]["reason"])
        before = {o["formid"]: o for o in out["attribution"]["comparisons"][0]["before"]["objects"]}
        self.assertNotEqual("absent", before[b]["status"])

    def test_before_side_enumerated_absence_is_still_earned_new(self):
        """Do not make everything unresolved. With the before scene's record row present and its
        own state saying deleted or disabled, `new` is still earned on either kind of evidence."""
        a, b = "000001:A.esp", "000002:B.esp"
        combined = _setup("combined", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        for label, record_b in (("disabled", self._disabled(b, "b")),
                                ("deleted", _record(b, "b", deletion=True))):
            with self.subTest(evidence=label):
                source = _setup("source", [_record(a, "a"), record_b], [_pair(a, b)])
                out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
                self.assertEqual("new", out["attribution"]["status"])

    def test_after_side_deleted_endpoint_of_a_retained_pair_is_still_earned_absence(self):
        """The other earned-absence arm on the after side: deletion, not just a disabled state."""
        a, b = "000001:A.esp", "000002:B.esp"
        source = _setup("source", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        combined = _setup("combined", [_record(a, "a"), _record(b, "b", deletion=True)],
                          [self._retained(a, b, "the reference is deleted")])
        out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
        self.assertEqual("absent_excluded", out["attribution"]["status"])
        after = {o["formid"]: o for o in out["attribution"]["comparisons"][0]["after"]["objects"]}
        self.assertEqual("absent", after[b]["status"])
        self.assertTrue(after[b]["state"]["deletion"])

    def test_imported_reference_with_history_but_no_usable_pose_is_unresolved(self):
        """The gap the producer-side argument missed. overlapprobe's record loop skips a
        reference with no winning geometry pose, while missingImportedReferences filters on
        missing HISTORY -- so this reference is enumerated nowhere AND declared missing nowhere.
        The consumer must not read that silence as absence on either side."""
        a, b = "000001:A.esp", "000002:B.esp"
        reason = "a historical reference is absent from the evaluated order"
        source = self._imported(_setup("source", [_record(a, "a"), _record(b, "b")], [_pair(a, b)]))
        combined = self._imported(_setup("combined", [_record(a, "a")], [self._retained(a, b, reason)]))
        out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
        self.assertEqual([], combined["scene"]["discovery"]["missingImportedReferences"])
        self.assertEqual("unresolved", out["attribution"]["status"])
        mirrored = _attribute({"source": self._imported(_setup("source", [_record(a, "a")],
                                  [self._retained(a, b, reason)])),
                               "combined": self._imported(_setup("combined",
                                  [_record(a, "a"), _record(b, "b")], [_pair(a, b)]))},
                              ["source"])["pairs"][0]
        self.assertEqual("unresolved", mirrored["attribution"]["status"])

    def test_unproven_endpoint_does_not_suppress_a_higher_precedence_finding(self):
        """Aggregation is unchanged: unresolved already tops STATUS_PRECEDENCE, so one unproven
        comparison makes the row unresolved while the resolved comparisons keep their own verdicts."""
        a, b = "000001:A.esp", "000002:B.esp"
        good = _setup("good", [_record(a, "a"), _record(b, "b", position=[12, 0, 0])], [_pair(a, b)])
        unproven = _setup("unproven", [_record(a, "a")],
                          [self._retained(a, b, "a historical reference is absent from the evaluated order")])
        combined = _setup("combined", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        out = _attribute({"good": good, "unproven": unproven, "combined": combined},
                         ["good", "unproven"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])
        by_setup = {c["beforeSetupId"]: c["status"] for c in out["attribution"]["comparisons"]}
        self.assertEqual({"good": "changed_existing", "unproven": "unresolved"}, by_setup)

    def test_an_unretained_pair_is_still_unresolved_not_absent(self):
        """The absence rule's floor, unchanged by this work: with no retained row the consumer
        must not infer absence. An empty result never means absence."""
        a, b = "000001:A.esp", "000002:B.esp"
        source = _setup("source", [_record(a, "a"), _record(b, "b")], [_pair(a, b)])
        combined = _setup("combined", [_record(a, "a")], [])
        out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])


class OwnerNotLoadedAbsenceTests(unittest.TestCase):
    """P41: a FormKey can only be declared by its owning plugin or a plugin mastering it. When the setup's
    configuration neither loads nor masters the owner, the object does not exist there: absence decided
    from the plugin set. A reference the scene merely failed to enumerate stays unresolved."""
    UNPROVEN = "a retained pair row names an endpoint this scene never enumerated, so its absence is unproven"
    a, b = "000001:Base.esm", "000801:Cand.esp"

    @staticmethod
    def _plugins(setup, *plugins):
        """plugins: (name, [masters]) tuples, written as the producer writes scope.configuration.Plugins."""
        s = copy.deepcopy(setup)
        s["scope"]["configuration"]["Plugins"] = [{"Name": n, "Masters": list(m)} for n, m in plugins]
        return s

    @staticmethod
    def _retained(a, b, reason="a historical reference is absent from the evaluated order"):
        row = copy.deepcopy(_pair(a, b))
        row["actualShape"] = {"status": "unknown", "reason": reason, "surfaceRelation": "unknown",
                              "containment": "unknown", "intersects": None, "trianglePairsTested": 0}
        return row

    def _combined(self):
        return self._plugins(_setup("combined", [_record(self.a, "a"), _record(self.b, "b")], [_pair(self.a, self.b)]),
                             ("Base.esm", []), ("Cand.esp", ["Base.esm"]))

    def _source(self, *plugins, reason="a historical reference is absent from the evaluated order"):
        return self._plugins(_setup("source", [_record(self.a, "a")], [self._retained(self.a, self.b, reason)]),
                             *plugins)

    @staticmethod
    def _cmp(out, side="before"):
        c = out["attribution"]["comparisons"][0]
        return c, {o["formid"]: o for o in c[side]["objects"]}

    def test_c500_before_side_owner_not_loaded_is_new(self):
        out = _attribute({"source": self._source(("Base.esm", [])), "combined": self._combined()},
                         ["source"])["pairs"][0]
        c, before = self._cmp(out)
        self.assertEqual("new", c["status"])
        self.assertEqual("new", out["attribution"]["status"])
        self.assertEqual("absent", before[self.b]["status"])
        self.assertEqual({"basis": "owning-plugin-not-loaded", "owningPlugin": "Cand.esp"}, before[self.b]["absence"])

    def test_c501_owner_loaded_but_unenumerated_stays_unresolved(self):
        src = self._source(("Base.esm", []), ("Cand.esp", ["Base.esm"]))
        out = _attribute({"source": src, "combined": self._combined()}, ["source"])["pairs"][0]
        c, before = self._cmp(out)
        self.assertEqual("unresolved", c["status"])
        self.assertEqual(self.UNPROVEN, c["reason"])
        self.assertEqual("unknown", before[self.b]["status"])

    def test_c502_owner_mastered_by_a_loaded_plugin_stays_unresolved(self):
        src = self._source(("Base.esm", []), ("Other.esp", ["Base.esm", "Cand.esp"]))
        out = _attribute({"source": src, "combined": self._combined()}, ["source"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])
        self.assertEqual(self.UNPROVEN, self._cmp(out)[0]["reason"])

    def test_c503_setup_without_plugins_stays_unresolved(self):
        src = _setup("source", [_record(self.a, "a")], [self._retained(self.a, self.b)])
        out = _attribute({"source": src, "combined": self._combined()}, ["source"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])
        self.assertEqual("unknown", self._cmp(out)[1][self.b]["status"])

    def test_c504_after_side_owner_not_loaded_is_absent_excluded(self):
        source = self._plugins(_setup("source", [_record(self.a, "a"), _record(self.b, "b")], [_pair(self.a, self.b)]),
                               ("Base.esm", []), ("Cand.esp", ["Base.esm"]))
        combined = self._plugins(_setup("combined", [_record(self.a, "a")], [self._retained(self.a, self.b)]),
                                 ("Base.esm", []))
        out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
        c, after = self._cmp(out, "after")
        self.assertEqual("absent_excluded", c["status"])
        self.assertEqual("owning-plugin-not-loaded", after[self.b]["absence"]["basis"])

    def test_c505_reason_prose_does_not_move_either_status(self):
        for prose in ("a historical reference is absent from the evaluated order",
                      "the reference is absent in the selected state", "", "totally reworded next release"):
            with self.subTest(prose=prose):
                unloaded = self._source(("Base.esm", []), reason=prose)
                loaded = self._source(("Base.esm", []), ("Cand.esp", ["Base.esm"]), reason=prose)
                self.assertEqual("new", _attribute({"source": unloaded, "combined": self._combined()},
                                                   ["source"])["pairs"][0]["attribution"]["status"])
                self.assertEqual("unresolved", _attribute({"source": loaded, "combined": self._combined()},
                                                          ["source"])["pairs"][0]["attribution"]["status"])

    def test_c506_owner_match_is_case_insensitive(self):
        src = self._source(("Base.esm", []), ("CAND.ESP", ["base.esm"]))
        out = _attribute({"source": src, "combined": self._combined()}, ["source"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])

    def test_c507_two_candidates_three_comparisons_all_new(self):
        a, b = "000801:CandA.esp", "000802:CandB.esp"
        base, ca, cb = ("Base.esm", []), ("CandA.esp", ["Base.esm"]), ("CandB.esp", ["Base.esm"])
        combined = self._plugins(_setup("combined", [_record(a, "a"), _record(b, "b")], [_pair(a, b)]), base, ca, cb)
        dep = self._plugins(_setup("dependency", [], [self._retained(a, b)]), base)
        only_a = self._plugins(_setup("only-a", [_record(a, "a")], [self._retained(a, b)]), base, ca)
        only_b = self._plugins(_setup("only-b", [_record(b, "b")], [self._retained(a, b)]), base, cb)
        full = _attribute({"combined": combined, "dependency": dep, "only-a": only_a, "only-b": only_b},
                          ["dependency", "only-a", "only-b"])
        out = full["pairs"][0]
        self.assertEqual({"dependency": "new", "only-a": "new", "only-b": "new"},
                         {c["beforeSetupId"]: c["status"] for c in out["attribution"]["comparisons"]})
        self.assertEqual("new", out["attribution"]["status"])
        self.assertTrue(full["accounting"]["attribution"]["complete"])

    def test_c508_owner_absence_does_not_override_a_hard_unresolved_cause(self):
        src = self._source(("Base.esm", []))
        src["readerVersion"] = "reader-v2"
        out = _attribute({"source": src, "combined": self._combined()}, ["source"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])
        self.assertEqual("reader/predicate/tolerance/limit identities do not match", self._cmp(out)[0]["reason"])
        invalid = self._source(("Base.esm", []))
        invalid["scene"]["validity"] = {"status": "unresolved", "reasons": ["fixture incomplete"]}
        out = _attribute({"source": invalid, "combined": self._combined()}, ["source"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])

    def test_c509_after_missing_pair_with_owner_absent_endpoint_stays_unresolved(self):
        source = self._plugins(_setup("source", [_record(self.a, "a"), _record(self.b, "b")], [_pair(self.a, self.b)]),
                               ("Base.esm", []), ("Cand.esp", ["Base.esm"]))
        combined = self._plugins(_setup("combined", [_record(self.a, "a")], []), ("Base.esm", []))
        out = _attribute({"source": source, "combined": combined}, ["source"])["pairs"][0]
        self.assertEqual("unresolved", out["attribution"]["status"])

    def test_c510_injected_record_whose_owner_is_a_listed_master_is_not_owner_absent(self):
        """A loaded plugin that overrides or places a FormKey of one of its masters can declare the object even
        when the owner itself is not in Plugins. Without a record row that stays unresolved; with one, the
        record's own state decides (present here), never the owner rule."""
        injector = ("Injector.esp", ["Base.esm", "Cand.esp"])
        no_row = self._source(("Base.esm", []), injector)
        out = _attribute({"source": no_row, "combined": self._combined()}, ["source"])["pairs"][0]
        c, before = self._cmp(out)
        self.assertEqual("unresolved", c["status"])
        self.assertEqual(self.UNPROVEN, c["reason"])
        self.assertNotIn("absence", before[self.b])
        with_row = self._plugins(_setup("source", [_record(self.a, "a"), _record(self.b, "b")],
                                        [_pair(self.a, self.b)]), ("Base.esm", []), injector)
        out = _attribute({"source": with_row, "combined": self._combined()}, ["source"])["pairs"][0]
        c, before = self._cmp(out)
        self.assertEqual("present", before[self.b]["status"])
        self.assertEqual("inherited", c["status"])


if __name__ == "__main__": unittest.main()
