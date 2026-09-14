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

if __name__ == "__main__": unittest.main()
