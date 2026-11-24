using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;
using static HarnessShared.NifBoxFixture;

// The NIF writer (NewFile, Save, Load, Attach) is _shared/NifBoxFixture.cs.
static class MaterialScaleProof
{
    static HavokMaterial Mat(uint value) => new() { Material_SHM = (SkyrimHavokMaterial)value };
    public static void Run(Action<string, bool, string> check)
    {
        const string wood = "SKY_HAV_MAT_WOOD", stairs = "SKY_HAV_MAT_STAIRS_WOOD";
        // Literal values from nif.xml, independent of the decoder's enum lookup.
        const uint woodId = 500811281, stairsId = 1461712277;
        foreach (var (value, name) in new[] { (woodId, wood), (stairsId, stairs), (899511101u, "SKY_HAV_MAT_STAIRS_STONE"),
                                            (1560365355u, "SKY_HAV_MAT_STAIRS_SNOW"), (2892392795u, "SKY_HAV_MAT_STAIRS_BROKEN_STONE"), (880200008u, "SKY_HAV_MAT_STAIRS_GLASS") })
        {
            var f = NewFile();
            Attach(f, new bhkCapsuleShape { FirstPoint = Vector3.Zero, SecondPoint = Vector3.UnitZ, Radius = .1f, Radius1 = .1f, Radius2 = .1f, Material = Mat(value) });
            var bytes = Save(f);
            var decoded = HavokCollision.Decode(Load(bytes), true).Bodies.Single();
            check($"material: capsule bytes {value} decode as {name}", decoded.Indices.Length > 0 && decoded.TriangleMaterials!.Length == decoded.Indices.Length / 3 && decoded.TriangleMaterials.All(m => m == name), string.Join(", ", decoded.TriangleMaterials!.Distinct()));
        }
        // Mixed compressed mesh: a big triangle, a strip chunk, and an instance that has its OWN material.
        {
            var f = NewFile();
            var data = new bhkCompressedMeshShapeData
            {
                ChunkMaterials = new() { new() { Material = (SkyrimHavokMaterial)woodId }, new() { Material = (SkyrimHavokMaterial)stairsId } },
                BigVerts = new() { new(0,0,0,0), new(1,0,0,0), new(0,1,0,0) },
                BigTris = new() { new() { Triangle = new Triangle(0,1,2), Material = 1 } },
                Chunks = new()
                {
                    new() { MaterialIndex = 0, Reference = ushort.MaxValue, TransformIndex = ushort.MaxValue, NumVertices = 12, NumIndices = 4, NumStrips = 1,
                        Vertices = new() { new() { X=0,Y=0,Z=0 }, new() { X=1000,Y=0,Z=0 }, new() { X=1000,Y=1000,Z=0 }, new() { X=0,Y=1000,Z=0 } },
                        Indices = new() { 0,1,2,3 }, Strips = new() { 4 } },
                    new() { MaterialIndex = 1, Reference = 0, TransformIndex = ushort.MaxValue, Translation = new(2,0,0,0) },
                    new() { MaterialIndex = 99, Reference = 0, TransformIndex = ushort.MaxValue, Translation = new(4,0,0,0) }
                }
            };
            Attach(f, new bhkCompressedMeshShape { Data = new NiBlockRef<bhkCompressedMeshShapeData>(f.AddBlock(data)) });
            var b = HavokCollision.Decode(Load(Save(f)), true).Bodies.Single();
            var expected = new string?[] { stairs, wood, wood, stairs, stairs, null, null };
            check("material: compressed big triangle, strip, instanced material and invalid index stay aligned", b.TriangleMaterials!.SequenceEqual(expected), string.Join(",", b.TriangleMaterials!.Select(m => m ?? "unknown")));
            data.MaterialType = (bhkCMSMatType)2;
            var unknownEncoding = HavokCollision.Decode(Load(Save(f)), true).Bodies.Single();
            check("material: unsupported compressed encoding is unknown while geometry remains readable", unknownEncoding.Indices.Length == b.Indices.Length && unknownEncoding.TriangleMaterials!.All(m => m is null), $"{unknownEncoding.Indices.Length / 3} triangles");
        }
        {
            var f = NewFile();
            Attach(f, new bhkBoxShape { Dimensions = Vector3.One, Material = Mat(woodId) }, phantom: true);
            Attach(f, new bhkBoxShape { Dimensions = Vector3.One, Material = Mat(stairsId) });
            var model = NifGeometry.Measure(Save(f), includeTriangles: true).Model!;
            var solid = model.CollisionSoupFor(b => b.Solid)!;
            check("material: body filter removes phantom triangles and preserves solid material ordinals", model.CollisionSoup!.TriangleCount == 24 && solid.TriangleCount == 12 && solid.TriangleMaterials!.All(m => m == stairs), $"{model.CollisionSoup.TriangleCount}/{solid.TriangleCount}");
        }

        var navKey = FormKey.Factory("000001:Proof.esp");
        var strip = new NavmeshGeometry(navKey,
            new[] { new Vec3(-600,3,0), new Vec3(600,3,0), new Vec3(600,131,0), new Vec3(-600,131,0) },
            new[] { new NavTri(0,1,2,0,-1,-1,1), new NavTri(0,2,3,0,0,-1,-1) },
            new Vec3(-600,3,0), new Vec3(600,131,0), Array.Empty<NavEdgeLink>(), Array.Empty<NavDoorTriangle>());
        var navs = NavmeshIndex.Build(new[] { strip }, out _);
        (RouteFacts Facts, RouteCutVerdict Verdict, string Why) Beam(float rise, string? material, float bottom = 0, bool mixed = false, bool narrow = false)
        {
            var lo = new Vector3(-20, narrow ? 50 : -50, bottom); var hi = new Vector3(20, narrow ? 80 : 200, rise);
            var (v,i) = HavokCollision.BoxSoup((hi-lo)/2); var center=(lo+hi)/2;
            var mats=Enumerable.Repeat(material,i.Count/3).ToArray();
            if (mixed) { mats[10]=wood; mats[11]=wood; }
            var cols=MeshColumnIndex.Build(v.Select(p=>p+center).ToArray(),i.ToArray(),triangleMaterials:mats);
            var facts=RouteConnectivity.Measure(navs,cols,Aabb.Empty.Add(lo).Add(hi),256,16,ActorBand.PhysicalStep,130,new[]{0f,32,64,96,128},
                floorAt: (_,_,_)=>0, isStairsMaterial: DetectorModel.IsStairsMaterial);
            var l=facts.Line;
            var c=DetectorModel.RouteCut(facts.Judged,facts.Touched,l?.Found??false,l?.Why??"",l?.Cut??false,l?.Passage??float.NaN,
                cutRise:l?.CutRise??float.NaN,cutFloat:l?.CutFloat??float.NaN,cutOnStairs:l?.CutOnStairs??false);
            return (facts,c.Verdict,c.Why);
        }
        foreach(float rise in new[]{68f,72f,74f,75.99f,76f,80f})
        {
            foreach(string mat in new[]{wood,stairs})
            {
                var b=Beam(rise,mat);
                bool step=mat==stairs && rise<76;
                check($"material ladder: {mat} {rise} -> {(step?"step":"wall")}",
                    b.Verdict==(step?RouteCutVerdict.None:RouteCutVerdict.Blocked) && b.Facts.Line!.CutOnStairs==(mat==stairs)
                    && (step ? !b.Why.Contains("step band") : rise>=76 || b.Why.Contains("step band")), $"{b.Verdict}: {b.Why}");
            }
        }
        var unknown=Beam(72,null); var mixed=Beam(72,stairs,mixed:true); var floating=Beam(72,stairs,bottom:22);
        check("material: unknown material never grants the stairs exception",unknown.Verdict==RouteCutVerdict.Blocked && !unknown.Facts.Line!.CutOnStairs,unknown.Why);
        check("material: a wood face inside the cut on a stairs mesh keeps the wall",mixed.Verdict==RouteCutVerdict.Blocked && !mixed.Facts.Line!.CutOnStairs,mixed.Why);
        check("material: stairs float clause unchanged and its unproven transfer named",floating.Verdict==RouteCutVerdict.Blocked && floating.Why.Contains("floats") && floating.Why.Contains("unproven"),floating.Why);
        var narrowStep=Beam(72,stairs,narrow:true); var narrowWood=Beam(72,wood,narrow:true);
        check("material: an open side passage does not keep a stairs step LikelyBlocked",narrowStep.Verdict==RouteCutVerdict.None && narrowWood.Verdict==RouteCutVerdict.LikelyBlocked,$"{narrowStep.Verdict}/{narrowWood.Verdict}");
        // Off-line material must never taint the contact.
        var (vv,ii)=HavokCollision.BoxSoup(new Vector3(10,10,36));
        var allV=vv.Concat(vv.Select(p=>p+new Vector3(200,0,0))).ToArray();
        var allI=ii.Concat(ii.Select(k=>k+vv.Count)).ToArray();
        var columns=MeshColumnIndex.Build(allV,allI,triangleMaterials:Enumerable.Repeat<string?>(stairs,12).Concat(Enumerable.Repeat<string?>(wood,12)).ToArray());
        columns.IntersectsDiscInSlab(0,0,22,0,30,DetectorModel.IsStairsMaterial,out bool localStairs);
        check("material: remote wood on a mixed mesh does not change stairs contact",localStairs,"contact at origin");

        var generated=SkeletonController.Inspect(Save(NewFile()));
        var customFile=NewFile(); Attach(customFile,new bhkCapsuleShape { Radius=.2f, Radius1=.2f, Radius2=.2f, SecondPoint=Vector3.UnitZ },phantom:true);
        var custom=SkeletonController.Inspect(Save(customFile));
        check("controller census: skeleton without controller is generated",generated.ControllerCustom==false,generated.Why);
        check("controller census: linked phantom shape is custom",custom.ControllerCustom==true,custom.Why);
        var ragdoll = NewFile(); Attach(ragdoll, new bhkBoxShape { Dimensions = Vector3.One });
        check("controller census: a ragdoll rigid body is not a custom controller", SkeletonController.Inspect(Save(ragdoll)).ControllerCustom == false, "rigid body without phantom");
        check("controller census: no skeleton root is unknown", SkeletonController.Inspect(new NifFile()).ControllerCustom is null, "empty file object");
        var broken=NewFile(); broken.AddBlock(new bhkSPCollisionObject());
        var brokenFact=SkeletonController.Inspect(broken);
        check("controller census: broken controller link is unknown, never generated",brokenFact.ControllerCustom is null,brokenFact.Why);
        check("controller census: unreadable bytes are unknown",SkeletonController.Inspect(new byte[]{1,2,3}).ControllerCustom is null,"invalid NIF");

        var race=FormKey.Factory("000002:Proof.esp"); var npc=FormKey.Factory("000003:Proof.esp");
        var races=new Dictionary<FormKey,ActorBand.RaceFacts>{{race,new(1,1,false,"Creature",MaleController:generated,FemaleController:custom)}};
        var npcs=new Dictionary<FormKey,ActorBand.NpcFacts>{{npc,new(race,false,1,false)}};
        PlacedRef P(float scale)=>new(FormKey.Factory("000004:Proof.esp"),"PlacedNpc",null,new Vec3(0,0,0),new Vec3(0,0,0),scale,npc,false,false,false);
        var one=ActorBand.ForCell(new[]{P(1)},npcs,races,138,"fallback");
        foreach(float scale in new[]{.5f,1.5f,2f})
        {
            var band=ActorBand.ForCell(new[]{P(scale)},npcs,races,138,"fallback");
            check($"controller: generated placement scale {scale} multiplies the top",MathF.Abs(band.Top-one.Top*scale)<.001f && band.Source.Contains("generated") && band.Source.Contains(P(scale).FormKey.ToString()),band.Source);
        }
        npcs[npc]=new(race,true,1,false);
        var female=ActorBand.ForCell(new[]{P(1.5f)},npcs,races,138,"fallback");
        check("controller: sex-specific custom shape ignores placement scale",female.Top==128 && female.Source.Contains("not counted"),female.Source);
        races[race]=races[race] with { FemaleController=brokenFact };
        var missing=ActorBand.ForCell(new[]{P(1.5f)},npcs,races,138,"fallback");
        check("controller: unclassified scaled race is explicitly unjudgeable",float.IsNaN(missing.Top) && missing.Source.Contains("unclassified"),missing.Source);
        var identity=ActorBand.ForCell(new[]{P(1)},npcs,races,138,"fallback");
        check("controller: unknown at unit scale is reported; identity scale needs no guess",identity.Top==128 && identity.Source.Contains("unclassified"),identity.Source);
    }
}
