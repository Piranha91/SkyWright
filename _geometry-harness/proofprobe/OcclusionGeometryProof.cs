using System.Numerics;
using HousecarlCore;

static class OcclusionGeometryProof
{
    public static void Run(Action<string,bool,string> check)
    {
        var quad = new[] {new Vector3(0,-100,-100),new Vector3(0,100,-100),new Vector3(0,100,100),new Vector3(0,-100,100)};
        var left = ConvexShadow.FromRectangle(quad,new(-100,0,0))!;
        var right = ConvexShadow.FromRectangle(quad,new(100,0,0))!;
        void Relation(string name, ConvexShadow v, SphereBound s, ShadowRelation expected)
            => check(name,v.Classify(s)==expected,$"{v.Classify(s)} expected {expected}");
        Relation("shadow: central small bound inside",left,new(new(30,0,0),20),ShadowRelation.Inside);
        Relation("shadow: same centre, loose radius crosses",left,new(new(30,0,0),40),ShadowRelation.Crossing);
        Relation("shadow: exact contact not promoted to cull",left,new(new(20,0,0),20),ShadowRelation.Crossing);
        Relation("shadow: near side outside",left,new(new(-30,0,0),20),ShadowRelation.Outside);
        Relation("shadow: opposite eye reverses far side",right,new(new(-30,0,0),20),ShadowRelation.Inside);
        Relation("shadow: distance alone fails at side",left,new(new(100,240,0),20),ShadowRelation.Outside);
        Relation("shadow: side crossing kept",left,new(new(100,190,0),20),ShadowRelation.Crossing);
        Relation("shadow: side inside",left,new(new(100,160,0),20),ShadowRelation.Inside);
        Relation("shadow: side exact tangent crossing",left,new(new(100,200,0),0),ShadowRelation.Crossing);
        Relation("shadow: invalid sphere explicit",left,new(new(float.NaN,0,0),20),ShadowRelation.Invalid);
        Relation("shadow: negative radius explicit",left,new(new(30,0,0),-1),ShadowRelation.Invalid);
        check("shadow: no volume for coplanar eye",ConvexShadow.FromRectangle(quad,Vector3.Zero) is null,"");
        check("shadow: malformed rectangle refused",ConvexShadow.FromRectangle(quad.Take(3).ToArray(),new(-100,0,0)) is null,"");
        var degenerate=quad.ToArray(); degenerate[1]=degenerate[0];
        check("shadow: degenerate rectangle refused",ConvexShadow.FromRectangle(degenerate,new(-100,0,0)) is null,"");
        var rotation=Matrix4x4.CreateFromYawPitchRoll(.43f,-.77f,1.1f);
        var shift=new Vector3(270,-880,450);
        Vector3 Transform(Vector3 p) => Vector3.Transform(p,rotation)+shift;
        var transformed=ConvexShadow.FromRectangle(quad.Select(Transform).ToArray(),Transform(new(-100,0,0)))!;
        Relation("shadow: full rotation and translation preserve containment",transformed,new(Transform(new(30,0,0)),20),ShadowRelation.Inside);
        check("shadow: reports eye distance without inventing exemption",Math.Abs(left.EyeDistance-100)<1e-4f,left.EyeDistance.ToString());
        var stored = new NifShapeBound(new(2,0,0),10,new(2,0,0),10,new[]{3},0);
        var world=SphereBound.Place(stored,new(0,0,MathF.PI/2),.5f,new(100,200,300));
        check("bound: reference translation rotation and scale",world is { } s && Vector3.Distance(s.Center,new(100,199,300))<.001f && s.Radius==5,$"{world}");
        check("bound: zero reference scale is unsupported",SphereBound.Place(stored,default,0,default) is null,"");
        var scene = new float[] {.2f,.2f,.8f,1};
        var rear=ShapeVisibility.Compare(new float[]{.8f,.8f,1,1},scene);
        check("visibility: culled rear shape hidden by drawn front shape is healthy",rear.Assessed && rear.VisiblePixels==0 && rear.HiddenPixels==2,$"{rear}");
        var edge=ShapeVisibility.Compare(new float[]{.8f,.8f,.8f,1},scene);
        check("visibility: hidden centre with visible own edge is a finding candidate",edge.Assessed && edge.VisiblePixels==1 && edge.HiddenPixels==2,$"{edge}");
        var empty=ShapeVisibility.Compare(new float[]{1,1,1,1},scene);
        check("visibility: empty interior never supplies subject pixels",empty.Assessed && empty.RasterPixels==0,$"{empty}");
        var mismatch=ShapeVisibility.Compare(new float[]{1},scene);
        check("visibility: mismatched buffers unassessed",!mismatch.Assessed,$"{mismatch}");
        var nan=ShapeVisibility.Compare(new float[]{float.NaN},new float[]{1});
        check("visibility: invalid depth unassessed",!nan.Assessed,$"{nan}");
        var equal=ShapeVisibility.Compare(new float[]{.2f},new float[]{.2f});
        check("visibility: same fragment survives self depth",equal.VisiblePixels==1,$"{equal}");
    }
}
