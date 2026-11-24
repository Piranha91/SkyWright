using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// Proposal-only RED controls. Root registers Run(Check) after additive inert support.
// Geometry/history controls use the real ContactIndex; off twins have zero radii and
// provide the independently measured ordinary/differential selection before coverage.
static class CoverageSelectionProof
{
    static TriangleSoup Plane(float x0, float y0, float x1, float y1, float z = 0) =>
        new(new[] { new Vector3(x0,y0,z), new Vector3(x1,y0,z), new Vector3(x1,y1,z), new Vector3(x0,y1,z) },
            new[] { 0,1,2,0,2,3 });

    static TriangleSoup Rug() => new(new[] { new Vector3(-2,-2,0), new Vector3(2,-2,0), new Vector3(0,2,0) }, new[] { 0,2,1 });

    static TriangleSoup Ring()
    {
        const float h = .000001f;
        var parts = new[] { Plane(-3,-3,3,-h), Plane(-3,h,3,3), Plane(-3,-h,-h,h), Plane(h,-h,3,h) };
        var v = new List<Vector3>(); var i = new List<int>();
        foreach (var p in parts) { int offset = v.Count; v.AddRange(p.Vertices); i.AddRange(p.Indices.Select(n => n + offset)); }
        return new(v.ToArray(), i.ToArray());
    }

    static string Detail(ContactRow? r) => r is null ? "no row" :
        $"{r.Relation}/{r.Verdict}/{r.Reason} against={r.Winner?.Against} d={r.Facts.WinnerDistance:R} p={r.Facts.WinnerPenetration:R} {r.Why}";

    static bool SameSelected(ContactRow? a, ContactRow? b) => a is not null && b is not null
        && a.Relation == b.Relation && a.Verdict == b.Verdict && a.Reason == b.Reason && a.Why == b.Why
        && a.Facts.AuthoringDistance.Equals(b.Facts.AuthoringDistance) && a.Facts.AuthoringPenetration.Equals(b.Facts.AuthoringPenetration)
        && a.Facts.WinnerDistance.Equals(b.Facts.WinnerDistance) && a.Facts.WinnerPenetration.Equals(b.Facts.WinnerPenetration)
        && a.Facts.WinnerContactIsHost == b.Facts.WinnerContactIsHost
        && a.Facts.WinnerAgainstExtent.Equals(b.Facts.WinnerAgainstExtent)
        && a.Facts.WinnerPreexistingPenetration.Equals(b.Facts.WinnerPreexistingPenetration)
        && a.Facts.WinnerPreexistingOnly == b.Facts.WinnerPreexistingOnly
        && a.Winner is { } x && b.Winner is { } y && x.Against == y.Against && x.Contact == y.Contact
        && x.DepSrc == y.DepSrc && x.HostSrc == y.HostSrc && x.AgainstMaxDim.Equals(y.AgainstMaxDim)
        && x.RawDistance.Equals(y.RawDistance) && x.RawAgainst == y.RawAgainst && x.CoreGap.Equals(y.CoreGap);

    static DetectorModel.CoverageFacts Facts(DirectionalCoverageResult r) => new(r.ProjectedArea, r.CoveredArea, r.UncoveredArea,
        r.MinGap, r.MaxGap, r.Complete, r.RefusalReason, r.FullCoverReading, r.UncertaintyReason);

    public static void Run(Action<string, bool, string> Check)
    {
        var root = Path.Combine(Path.GetTempPath(), "housecarl-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var mk = ModKey.FromNameAndExtension("SelectionMaster.esm");
        var ak = ModKey.FromNameAndExtension("SelectionMover.esp");
        var floorK = new FormKey(mk, 0xA00); var rugK = new FormKey(mk, 0xA01);
        var blockK = new FormKey(mk, 0xA02); var coverK = new FormKey(mk, 0xA03);
        var ringK = new FormKey(mk, 0xA04); var slopeK = new FormKey(mk, 0xA05); var missingK = new FormKey(mk, 0xA06);
        FormKey CellK(int n) => new(mk, (uint)(0x900+n));
        FormKey H(int n) => new(mk, (uint)(0x1000+n));
        FormKey D(int n) => new(mk, (uint)(0x2000+n));
        FormKey T(int n) => new(mk, (uint)(0x3000+n));
        FormKey C(int n) => new(mk, (uint)(0x4000+n));
        static Static Stat(FormKey k) => new(k, SkyrimRelease.SkyrimSE) { EditorID = "Selection" + k.ID,
            ObjectBounds = new ObjectBounds { First = new P3Int16(-100,-100,-20), Second = new P3Int16(100,100,20) } };
        static PlacedObject Ref(FormKey k, FormKey b, float x, float z)
        {
            var r = new PlacedObject(k, SkyrimRelease.SkyrimSE)
            { Placement = new Placement { Position = new P3Float(x,0,z), Rotation = new P3Float(0,0,0) } };
            r.Base.SetTo(b); return r;
        }
        static void File(SkyrimMod m, Cell c)
        {
            var block = new CellBlock { BlockNumber = (int)(c.FormKey.ID%10), GroupType = GroupTypeEnum.InteriorCellBlock };
            var sub = new CellSubBlock { BlockNumber = (int)(c.FormKey.ID/10%10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
            sub.Cells.Add(c); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        }
        string Write(SkyrimMod m, params ISkyrimModGetter[] masters)
        { var p = Path.Combine(root, m.ModKey.FileName.String); m.BeginWrite.ToPath(p).WithLoadOrder(masters).Write(); return p; }

        // Separate cells keep each tiny-hole fixture near coordinate zero, without cross-fixture neighbours.
        var master = new SkyrimMod(mk, SkyrimRelease.SkyrimSE);
        foreach (var k in new[] { floorK,rugK,blockK,coverK,ringK,slopeK,missingK }) master.Statics.Add(Stat(k));
        for (int n=1;n<=7;n++)
        {
            var cell = new Cell(CellK(n), SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
            cell.Temporary.Add(Ref(H(n), n==2 ? slopeK : floorK, 0,-1));
            cell.Temporary.Add(Ref(D(n), rugK, 0,0));
            cell.Temporary.Add(Ref(T(n), blockK, 60,0));
            cell.Temporary.Add(Ref(C(n), n==4 || n==5 ? ringK : n==6 ? missingK : coverK,
                n==4 || n==5 || n==6 ? 0 : 400, 1.5f));
            File(master,cell);
        }
        var mp = Write(master);
        using var mo = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE);
        var mover = new SkyrimMod(ak, SkyrimRelease.SkyrimSE);
        for (int n=1;n<=7;n++)
        {
            var cell = new Cell(CellK(n), SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
            cell.Temporary.Add(Ref(H(n), n==2 ? slopeK : floorK,20,-1));
            if (n!=4) cell.Temporary.Add(Ref(C(n), n==5 ? ringK : coverK,0,n==2 ? 4 : n==5 ? 1.625f : 1.5f));
            if (n==1) cell.Temporary.Add(Ref(D(n),rugK,1,0)); // deliberately re-placed, settled MovedTogether
            if (n==3) cell.Temporary.Add(Ref(D(n),rugK,0,0)); // untouched placement, but known mover-side record touch
            File(mover,cell);
        }
        var ap = Write(mover,mo);
        RefGeometry? Geometry(FormKey k)
        {
            TriangleSoup? s = k==floorK ? Plane(-100,-100,100,100) : k==rugK ? Rug()
                : k==coverK ? Plane(-50,-50,50,50) : k==ringK ? Ring()
                : k==blockK ? ContactIndex.BoxSoup(new Aabb(new Vec3(-100,-100,-20),new Vec3(100,100,20),false))
                : k==slopeK ? new TriangleSoup(Plane(-100,-100,100,100).Vertices.Select(v=>new Vector3(v.X,v.Y,.75f*v.X)).ToArray(),new[] {0,1,2,0,2,3}) : null;
            if (s is null) return null;
            var box = s.Vertices.Aggregate(Aabb.Empty,(b,v)=>b.Add(v));
            return new(box,s,s,k==rugK ? '-' : 'c',k==rugK ? 'r' : 'c',k!=rugK,Array.Empty<(string,string)>(),false);
        }
        ContactIndex Load(bool radius)
        {
            var ci = new ContactIndex(Geometry) { ReadRadius=radius };
            for (int p=0;p<2;p++) { using var m=SkyrimMod.CreateFromBinaryOverlay(p==0?mp:ap,SkyrimRelease.SkyrimSE); ci.Index.Ingest(p,m); }
            return ci;
        }
        var closure = DetectorModel.MasterClosure(new[] { new HashSet<int>(),new HashSet<int> {0} });
        List<ContactRow> Rows(ContactIndex ci)
        { var sk = new ContactSkips(); var hs=ci.MovedHosts(sk,closure); var ds=ci.DependentSnaps(sk); var ws=ci.PhysicalWinners(null,sk); return hs.SelectMany(h=>ci.Judge(h,ds,ws,closure,sk,keepNoRelation:true)).ToList(); }
        var on=Load(true); var off=Load(false); var rows=Rows(on); var legacy=Rows(off);
        ContactRow? R(int n) => rows.FirstOrDefault(r=>r.Host.Key==H(n)&&r.Dependent==D(n));
        ContactRow? L(int n) => legacy.FirstOrDefault(r=>r.Host.Key==H(n)&&r.Dependent==D(n));
        CoverageNeighbour? W(int n) => R(n)?.Winner?.CoverageNeighbours?.FirstOrDefault(c=>c.Against==C(n));
        Check("SEL-0: real histories have authored relations and a valid settled slope control",
            Enumerable.Range(1,7).Where(n=>n!=2).All(n=>R(n)?.Relation==ContactRelation.Supported)
            && R(2)?.Verdict==ContactVerdict.NoRelation && R(2)?.Authoring?.Contact.NormalUp is > .5f and < .866f,
            string.Join("; ",Enumerable.Range(1,7).Select(n=>$"{n}:{Detail(R(n))}")));
        Check("SEL-1: settled MovedTogether keeps all ordinary winner facts and raw whole-cover evidence",
            R(1)?.Verdict==ContactVerdict.MovedTogether && SameSelected(R(1),L(1))
            && W(1)?.Winner.FullCoverReading=="nominal_whole_cover", Detail(R(1))+" | ordinary: "+Detail(L(1)));
        Check("SEL-2: settled slope NoRelation keeps all ordinary winner facts and raw uncertain-cover evidence",
            R(2)?.Verdict==ContactVerdict.NoRelation && SameSelected(R(2),L(2))
            && W(2)?.Winner.FullCoverReading=="numerically_uncertain", Detail(R(2))+" | ordinary: "+Detail(L(2)));
        Check("SEL-3: a touched but not re-placed dependent remains eligible for new shallow burial",
            R(3) is { Verdict:ContactVerdict.Sunk,DepTouchedByMoverSide:true } && on.Index.History.Of(D(3))?.Count==1
            && W(3)?.Winner.FullCoverReading=="nominal_whole_cover", Detail(R(3)));
        Check("SEL-4: exact unchanged complete inputs retain a positive tiny hole without a new differential unknown",
            R(4)?.Verdict==ContactVerdict.ReSupported && SameSelected(R(4),L(4))
            && W(4) is { Winner.Complete:true,Winner.UncoveredArea:>0,Authored.Complete:true,Authored.UncoveredArea:>0 }
            && W(4)?.Winner.FullCoverReading=="numerically_uncertain" && W(4)?.InputComparison?.ExactlyEqual==true,
            Detail(R(4))+$" | U={W(4)?.Winner.UncoveredArea:R} input={W(4)?.InputComparison}");
        Check("SEL-5: a changed covering face with a tiny real hole remains an explicit uncertainty",
            R(5) is { Verdict:ContactVerdict.Unjudgeable,Reason:ContactBlocker.ShallowCoverageIncomplete }
            && W(5)?.Winner.UncoveredArea>0 && W(5)?.Authored?.UncoveredArea>0,Detail(R(5)));
        Check("SEL-6: missing authored geometry never becomes a known zero or identical-input forgiveness",
            R(6) is { Verdict:ContactVerdict.Unjudgeable,Reason:ContactBlocker.ShallowCoverageIncomplete }
            && W(6)?.Authored is { Complete:false,RefusalReason:"authored_host_geometry_missing" },Detail(R(6)));
        Check("SEL-7: a moved whole covering face preserves the P71-type shallow finding and true core depth",
            R(7)?.Verdict==ContactVerdict.Sunk && R(7)?.Winner?.Against==C(7) && R(7)?.Facts.WinnerPenetration==0
            && W(7)?.Winner.UncoveredArea==0 && W(7)?.Authored?.CoveredArea==0,Detail(R(7)));

        var dep=Rug(); var host=Plane(-3,-3,3,3,1.5f); var pose=new Snap(0,default,CellK(1),false,false,FormKey.Null);
        var enabled=new EnableResolution(true,false,true);
        var frame=new CoverageInputs(rugK,coverK,'r','c',pose,pose,enabled,enabled,dep,TriangleGrid.Build(host),Vector3.UnitZ,4,.866f);
        var clone=frame with { DependentPose=pose with {Plugin=1},HostPose=pose with {Plugin=1},
            Dependent=new TriangleSoup((Vector3[])dep.Vertices.Clone(),(int[])dep.Indices.Clone()),
            Host=TriangleGrid.Build(new TriangleSoup((Vector3[])host.Vertices.Clone(),(int[])host.Indices.Clone())) };
        var exact=CoverageInputComparisonReader.CompareExact(frame,clone);
        Check("SEL-8: cloned complete consumed inputs compare exactly despite provenance-only plugin change",exact.ExactlyEqual && exact.ElementsCompared>0,exact.ToString());
        var jitter=frame with { DependentPose=pose with {Pos=new Vec3(.000001f,0,0)} };
        var jit=CoverageInputComparisonReader.CompareExact(frame,jitter);
        Check("SEL-9: tolerant changed-history equality cannot forgive an exact modeled pose change",
            pose.SameAs(jitter.DependentPose) && jit.Status=="different" && jit.Reason=="input_pose_changed",jit.ToString());
        var mutations=new[] {
            frame with { HostBase=blockK }, frame with { DependentSource='c' },
            frame with { HostPose=pose with {Rot=new Vec3(0,0,.000001f)} }, frame with { HostPose=pose with {Scale=1.000001f} },
            frame with { HostEnable=new EnableResolution(true,true,true) }, frame with { MaxGap=4.000001f },
            frame with { FaceCos=.9f }, frame with { Direction=Vector3.UnitX }, frame with { Tolerance=2e-5 },
            frame with { MaxTrianglePairs=1 }, frame with { MaxPieces=1 },
            frame with { Dependent=new TriangleSoup(dep.Vertices,new[] {0,1,2}) }
        }.Select(f=>CoverageInputComparisonReader.CompareExact(frame,f)).ToArray();
        Check("SEL-10: base role rotation scale enable topology and every measurement parameter are exact inputs",
            mutations.All(c=>c.Status=="different"),string.Join("; ",mutations.Select(c=>c.ToString())));
        var shifted=frame with { Host=TriangleGrid.Build(Plane(-4,-3,2,3,1.5f)) };
        var ma=DirectionalCoverage.Measure(dep,frame.Host!,Vector3.UnitZ,4);
        var mb=DirectionalCoverage.Measure(dep,shifted.Host!,Vector3.UnitZ,4);
        var different=CoverageInputComparisonReader.CompareExact(frame,shifted);
        Check("SEL-11: equal aggregate areas and gaps cannot imply identical covering triangles",
            ma.ProjectedArea==mb.ProjectedArea && ma.CoveredArea==mb.CoveredArea && ma.MinGap==mb.MinGap && ma.MaxGap==mb.MaxGap
            && different.Status=="different" && different.Reason=="input_host_triangles_changed",different.ToString());
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        var missing=CoverageInputComparisonReader.CompareExact(frame,frame with {Dependent=null});
        var unresolved=CoverageInputComparisonReader.CompareExact(frame,frame with {HostEnable=new EnableResolution(false,false,false)});
        var budget=CoverageInputComparisonReader.CompareExact(frame,clone,maxElements:0);
        var cancel=CoverageInputComparisonReader.CompareExact(frame,clone,cancellationToken:cancelled.Token);
        Check("SEL-12: unknown input geometry enable comparison budgets and cancellation are named refusals",
            missing is {Status:"unknown",Reason:"input_geometry_missing"} && unresolved is {Status:"unknown",Reason:"input_enable_unresolved"}
            && budget is {Status:"unknown",Reason:"input_comparison_element_budget"} && cancel is {Status:"unknown",Reason:"input_comparison_cancelled"},
            $"{missing}; {unresolved}; {budget}; {cancel}");
        var tiny=DirectionalCoverage.Measure(dep,TriangleGrid.Build(SpatialOverlap.TransformSoup(Ring(),new Vec3(0,0,1.5f),default,1)),Vector3.UnitZ,4);
        var uncertain=Facts(tiny);
        var f=new DetectorModel.ContactFacts(true,true,true,0,0,1,false,false,false,true,0,0,false,DependentMotion.Fixed,
            WinnerCoverage:uncertain,WinnerAuthoredCoverage:uncertain,WinnerCoverageInputsExactlyUnchanged:true);
        var identityRule=DetectorModel.PlacementContact(f);
        Check("SEL-13: complete exact-input identity has zero differential change while raw numerical uncertainty remains",
            tiny.Complete && tiny.UncoveredArea>0 && tiny.FullCoverReading=="numerically_uncertain"
            && identityRule.Verdict==ContactVerdict.ReSupported && f.WinnerCoverage!.UncoveredArea>0,
            $"U={tiny.UncoveredArea:R} {identityRule}");
        var limited=DirectionalCoverage.Measure(dep,TriangleGrid.Build(host),Vector3.UnitZ,4,maxTrianglePairs:0);
        var unknowns=new[] {
            f with {WinnerCoverageInputsExactlyUnchanged=false},
            f with {WinnerAuthoredCoverage=null},
            f with {WinnerCoverage=Facts(limited)},
            f with {WinnerAuthoredCoverage=Facts(limited)}
        }.Select(x=>DetectorModel.PlacementContact(x)).ToArray();
        Check("SEL-14: absent identity proof missing authored geometry and incomplete work cannot be forgiven",
            !limited.Complete && unknowns.All(r=>r.Verdict==ContactVerdict.Unjudgeable && r.Reason==ContactBlocker.ShallowCoverageIncomplete),
            string.Join("; ",unknowns.Select(r=>r.ToString())));
        var deepRule=DetectorModel.PlacementContact(f with {WinnerPenetration=12,WinnerAgainstExtent=30});
        Check("SEL-15: genuine new penetration keeps priority over complete unchanged coverage",deepRule.Verdict==ContactVerdict.Sunk,deepRule.ToString());
        Check("SEL-16: radius-off twins keep ordinary measured selection and omit directional diagnostics",
            legacy.All(r=>r.Winner?.CoverageNeighbours is null) && L(3)?.Verdict==ContactVerdict.ReSupported && L(4)?.Verdict==ContactVerdict.ReSupported,
            $"rows={legacy.Count}; touched={Detail(L(3))}; unchanged={Detail(L(4))}");
        mo.Dispose(); Directory.Delete(root,true);
    }
}
