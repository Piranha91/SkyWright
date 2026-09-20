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
        BoxShadow(check);
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

    /// <summary>The eight corners of an axis-aligned box, in the factory's index convention
    /// (bit 1 = +x, bit 2 = +y, bit 4 = +z) — the convention a placed primitive's corners use.</summary>
    static Vector3[] Corners(Vector3 centre, Vector3 half)
    {
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++)
            c[i] = centre + new Vector3((i&1)==0 ? -half.X : half.X,
                                        (i&2)==0 ? -half.Y : half.Y,
                                        (i&4)==0 ? -half.Z : half.Z);
        return c;
    }

    /// <summary>A box primitive's view shadow — the region behind its silhouette as seen from the
    /// eye, together with the box's own volume. The measured cases below are stated in the
    /// fixture's OWN frame: the eye at the origin, +y the view axis, +x right, +z up, so no world
    /// position takes part. FACE-UNION — a union of per-face shadows — is NOT separated from the
    /// silhouette by that evidence and is untested here.</summary>
    static void BoxShadow(Action<string,bool,string> check)
    {
        void Relation(string name, ConvexShadow? v, SphereBound s, ShadowRelation expected)
            => check(name, v is not null && v.Classify(s)==expected,
                     v is null ? "no volume" : $"{v.Classify(s)} expected {expected}");

        // ---- the four engine cases, as synthetic geometry in the fixture's own frame ----------
        // A box 100x100x150 in half-extents, square-on, its centre 400 u down the view axis and
        // 70.5 u below the eye; beside it the wider rectangle at the same distance.
        var eye = Vector3.Zero;
        var fixtureBox = ConvexShadow.FromBox(Corners(new(0,400,-70.5f), new(100,100,150)), eye);
        var fixtureRect = ConvexShadow.FromRectangle(new[]{ new Vector3(-250,400,-120.5f),
            new Vector3(250,400,-120.5f), new Vector3(250,400,279.5f), new Vector3(-250,400,279.5f) }, eye);
        Relation("box shadow: engine row 1 — a bound beyond the box's volume but behind its silhouette is culled",
                 fixtureBox, new(new(0,700,-60.5f),75), ShadowRelation.Inside);
        Relation("box shadow: engine row 2 — a bound inside the box's own volume is culled",
                 fixtureBox, new(new(0,400,-90.5f),37.5f), ShadowRelation.Inside);
        Relation("box shadow: engine row 3 — a bound in front of the box is kept",
                 fixtureBox, new(new(0,150,-60.5f),75), ShadowRelation.Outside);
        // Row 4: a standing actor 647 u out and 153 u off the axis, whose CENTRE is 59.45 u inside
        // the silhouette's side plane. The engine drew him under the box and hid him under the
        // wider rectangle at the same place; both follow only because the bound has size, so the
        // pair below brackets the boundary rather than resting on one measured radius. The
        // measured standing-humanoid bound (run evidence) falls on the Crossing side of it.
        var actor = new Vector3(153,647,-57.27f);
        Relation("box shadow: engine row 4 — a point-sized bound at the actor's place would be culled",
                 fixtureBox, new(actor,59), ShadowRelation.Inside);
        Relation("box shadow: engine row 4 — a standing actor's bound is not promoted to a cull",
                 fixtureBox, new(actor,61), ShadowRelation.Crossing);
        Relation("box shadow: engine row 4 — the same bound IS inside the wider rectangle's shadow",
                 fixtureRect, new(actor,61), ShadowRelation.Inside);

        // ---- the geometric properties, on a box whose silhouette planes are exact in float ----
        var box = ConvexShadow.FromBox(Corners(new(0,200,0), new(100,100,100)), eye);
        Relation("box shadow: straight behind is inside", box, new(new(0,400,0),50), ShadowRelation.Inside);
        Relation("box shadow: the box's own volume is inside", box, new(new(0,200,0),50), ShadowRelation.Inside);
        Relation("box shadow: in front of the near face is outside", box, new(new(0,50,0),20), ShadowRelation.Outside);
        Relation("box shadow: beyond the silhouette is outside", box, new(new(500,400,0),50), ShadowRelation.Outside);
        Relation("box shadow: exact contact with a side plane is not promoted to a cull",
                 box, new(new(300,300,0),0), ShadowRelation.Crossing);
        Relation("box shadow: a bound tangent to a side plane crosses", box, new(new(400,400,0),50), ShadowRelation.Crossing);
        Relation("box shadow: a bound tangent to the near face crosses", box, new(new(0,100,0),50), ShadowRelation.Crossing);
        Relation("box shadow: invalid sphere explicit", box, new(new(float.NaN,400,0),50), ShadowRelation.Invalid);
        Relation("box shadow: negative radius explicit", box, new(new(0,400,0),-1), ShadowRelation.Invalid);
        check("box shadow: reports the eye's distance to the box, not to a face centre",
              box is not null && MathF.Abs(box.EyeDistance-100)<1e-3f, $"{box?.EyeDistance}");

        // ---- the oblique eye: three faces visible, and the silhouette is not a face's outline --
        var obliqueEye = new Vector3(-600,-600,400);
        var cube = Corners(Vector3.Zero, new(100,100,100));
        var oblique = ConvexShadow.FromBox(cube, obliqueEye);
        var facing = ConvexShadow.FromRectangle(new[]{cube[0],cube[4],cube[6],cube[2]}, obliqueEye);
        var beyond = new SphereBound(new(640,440,-440),30);
        Relation("box shadow: oblique eye — a bound behind the box is inside its silhouette",
                 oblique, beyond, ShadowRelation.Inside);
        Relation("box shadow: oblique eye — the face most facing the eye does not contain that bound",
                 facing, beyond, ShadowRelation.Outside);

        // ---- rigid motion, and the refusals --------------------------------------------------
        var rotation = Matrix4x4.CreateFromYawPitchRoll(.43f,-.77f,1.1f);
        var shift = new Vector3(270,-880,450);
        Vector3 Move(Vector3 p) => Vector3.Transform(p,rotation)+shift;
        var moved = ConvexShadow.FromBox(Corners(new(0,200,0), new(100,100,100)).Select(Move).ToArray(), Move(eye));
        Relation("box shadow: full rotation and translation preserve containment",
                 moved, new(Move(new(0,400,0)),50), ShadowRelation.Inside);
        Relation("box shadow: full rotation and translation preserve exclusion",
                 moved, new(Move(new(0,50,0)),20), ShadowRelation.Outside);
        check("box shadow: no volume for an eye inside the box",
              ConvexShadow.FromBox(Corners(new(0,200,0), new(100,100,100)), new(0,200,0)) is null,"");
        check("box shadow: no volume for an eye on a face",
              ConvexShadow.FromBox(Corners(new(0,200,0), new(100,100,100)), new(0,100,0)) is null,"");
        check("box shadow: wrong corner count refused",
              ConvexShadow.FromBox(Corners(new(0,200,0), new(100,100,100)).Take(7).ToArray(), eye) is null,"");
        var flat = Corners(new(0,200,0), new(100,0,100));
        check("box shadow: a flattened box is refused rather than treated as a rectangle",
              ConvexShadow.FromBox(flat, eye) is null,"");
        var skewed = Corners(new(0,200,0), new(100,100,100)); skewed[1] += new Vector3(0,60,0);
        check("box shadow: a corner set whose edges are not square is refused",
              ConvexShadow.FromBox(skewed, eye) is null,"");
        // Corner 7 spans none of the three edges the box is read from, so only the check that the
        // eight points ARE that box can catch it.
        var bent = Corners(new(0,200,0), new(100,100,100)); bent[7] += new Vector3(0,60,0);
        check("box shadow: corners that are not a box are refused",
              ConvexShadow.FromBox(bent, eye) is null,"");
        var infinite = Corners(new(0,200,0), new(100,100,100)); infinite[5] = new(float.PositiveInfinity,0,0);
        check("box shadow: a non-finite corner is refused",ConvexShadow.FromBox(infinite, eye) is null,"");
        check("box shadow: a non-finite eye is refused",
              ConvexShadow.FromBox(Corners(new(0,200,0), new(100,100,100)), new(float.NaN,0,0)) is null,"");
    }
}
