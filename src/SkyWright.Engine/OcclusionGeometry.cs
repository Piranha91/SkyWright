using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

/// <summary>Geometry only. Whether the engine uses a particular shadow or exempts a camera
/// is a separate, evidence-scoped policy; none of those thresholds is inferred here.</summary>
public readonly record struct SphereBound(Vector3 Center, float Radius)
{
    public bool Valid => Finite(Center) && float.IsFinite(Radius) && Radius >= 0;
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public static SphereBound? Place(NifShapeBound stored, Vec3 rotation, float scale, Vec3 position)
    {
        var source = new Vector3(stored.ModelCenter.X, stored.ModelCenter.Y, stored.ModelCenter.Z);
        var pos = new Vector3(position.X, position.Y, position.Z);
        if (!Finite(source) || !Finite(pos) || !float.IsFinite(scale) || scale == 0 ||
            !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z)) return null;
        var world = new SphereBound(Vector3.Transform(source * scale, SpatialOverlap.RefrRotation(rotation)) + pos,
                                    stored.ModelRadius * MathF.Abs(scale));
        return world.Valid ? world : null;
    }
}

public enum ShadowRelation { Invalid, Outside, Crossing, Inside }

/// <summary>A finite quadrilateral's perspective shadow, extending away from the eye.
/// Half-space normals are unit length; each returned clearance is in world units.</summary>
public sealed class ConvexShadow
{
    readonly (Vector3 Normal, float Offset)[] halves;
    public float EyeDistance { get; }
    ConvexShadow((Vector3, float)[] planes, float distance) { halves = planes; EyeDistance = distance; }

    public static ConvexShadow? FromRectangle(IReadOnlyList<Vector3> q, Vector3 eye)
    {
        if (q.Count != 4 || !SphereBound.Finite(eye) || q.Any(p => !SphereBound.Finite(p))) return null;
        var a = q[1]-q[0]; var b = q[3]-q[0];
        float la = a.Length(), lb = b.Length();
        if (!float.IsFinite(la+lb) || la < 1e-5f || lb < 1e-5f) return null;
        if (MathF.Abs(Vector3.Dot(a/la, b/lb)) > 1e-4f || (q[2]-(q[0]+a+b)).Length() > MathF.Max(la,lb)*1e-4f)
            return null;
        var normal = Vector3.Normalize(Vector3.Cross(a,b));
        var center = (q[0]+q[2])*.5f;
        float eyeDistance = Vector3.Dot(eye-center, normal);
        if (!float.IsFinite(eyeDistance) || MathF.Abs(eyeDistance) < 1e-5f) return null;
        var n = -MathF.Sign(eyeDistance)*normal;
        var planes = new (Vector3,float)[5];
        planes[0] = (n, -Vector3.Dot(n,center));
        for (int i=0;i<4;i++)
        {
            var edge = Vector3.Cross(q[i]-eye,q[(i+1)%4]-eye);
            float length = edge.Length();
            if (!float.IsFinite(length) || length < 1e-5f) return null;
            n = edge/length;
            if (Vector3.Dot(n,center-eye)<0) n=-n;
            planes[i+1]=(n,-Vector3.Dot(n,eye));
        }
        return new ConvexShadow(planes,MathF.Abs(eyeDistance));
    }

    /// <summary>An oriented box's perspective shadow: the region behind its silhouette as seen from
    /// <paramref name="eye"/>, together with the box's own volume — exactly the points whose segment
    /// from the eye meets the box. <paramref name="corners"/> are its eight corners indexed by axis
    /// bits (1 = +x, 2 = +y, 4 = +z) in the box's own frame. Built from one plane per SILHOUETTE
    /// edge — an edge whose two faces disagree about facing the eye, which is why an oblique eye,
    /// where the silhouette is not the near face's outline, is handled by construction — plus one
    /// half-space per front-facing face, the near cap that admits the box's own volume. An eye
    /// inside or on the box, a degenerate or non-box corner set, and non-finite input yield no
    /// volume; whether the engine culls with a particular shadow remains the caller's policy.</summary>
    public static ConvexShadow? FromBox(IReadOnlyList<Vector3> corners, Vector3 eye)
    {
        if (corners.Count != 8 || !SphereBound.Finite(eye)) return null;
        for (int i = 0; i < 8; i++) if (!SphereBound.Finite(corners[i])) return null;
        var origin = corners[0];
        var edges = new[] { corners[1]-origin, corners[2]-origin, corners[4]-origin };
        var axis = new Vector3[3]; var half = new float[3];
        for (int k = 0; k < 3; k++)
        {
            float length = edges[k].Length();
            if (!float.IsFinite(length) || length < 1e-5f) return null;
            axis[k] = edges[k]/length; half[k] = length*.5f;
        }
        for (int k = 0; k < 3; k++)
            if (MathF.Abs(Vector3.Dot(axis[k],axis[(k+1)%3])) > 1e-4f) return null;
        // The eight given points must BE that box: a caller's skewed or mis-ordered corner set is
        // refused rather than silently reinterpreted as the parallelepiped spanned by three of them.
        float slack = MathF.Max(MathF.Max(half[0],half[1]),half[2])*2e-4f;
        for (int i = 0; i < 8; i++)
        {
            var expected = origin;
            for (int k = 0; k < 3; k++) if ((i & (1<<k)) != 0) expected += edges[k];
            if ((corners[i]-expected).Length() > slack) return null;
        }
        var center = origin + (edges[0]+edges[1]+edges[2])*.5f;

        // Face f = axis k, side s (0 = -, 1 = +): outward normal and its plane offset.
        var normal = new Vector3[6]; var offset = new float[6]; var front = new bool[6];
        for (int k = 0; k < 3; k++)
            for (int s = 0; s < 2; s++)
            {
                int f = k*2+s;
                normal[f] = s == 0 ? -axis[k] : axis[k];
                offset[f] = Vector3.Dot(normal[f], center) + half[k];
                front[f] = Vector3.Dot(normal[f], eye) - offset[f] > 0;
            }
        var planes = new List<(Vector3,float)>(9);
        for (int f = 0; f < 6; f++)
            if (front[f]) planes.Add((-normal[f], offset[f]));   // the near cap: on the box's side
        if (planes.Count == 0) return null;                      // the eye is inside or on the box

        // Each pair of faces on different axes shares exactly one edge, so these twelve pairs ARE
        // the box's twelve edges; the edge is on the silhouette when its faces disagree.
        for (int k = 0; k < 3; k++)
            for (int m = k+1; m < 3; m++)
            {
                int other = 3-k-m;
                for (int sk = 0; sk < 2; sk++)
                    for (int sm = 0; sm < 2; sm++)
                    {
                        if (front[k*2+sk] == front[m*2+sm]) continue;
                        var a = corners[(sk<<k)|(sm<<m)]-eye;
                        var b = corners[(sk<<k)|(sm<<m)|(1<<other)]-eye;
                        var edge = Vector3.Cross(a,b);
                        float length = edge.Length(), scale = a.Length()*b.Length();
                        if (!float.IsFinite(length) || !float.IsFinite(scale) || length < scale*1e-6f) return null;
                        var n = edge/length;
                        if (Vector3.Dot(n,center-eye) < 0) n = -n;
                        planes.Add((n,-Vector3.Dot(n,eye)));
                    }
            }

        // The eye's distance to the BOX — its nearest surface point, not a face centre.
        var nearest = center;
        for (int k = 0; k < 3; k++)
            nearest += axis[k]*Math.Clamp(Vector3.Dot(eye-center,axis[k]),-half[k],half[k]);
        float distance = (eye-nearest).Length();
        if (!float.IsFinite(distance)) return null;
        return new ConvexShadow(planes.ToArray(), distance);
    }

    public float[] Clearances(SphereBound sphere)
        => halves.Select(p=>Vector3.Dot(p.Normal,sphere.Center)+p.Offset).ToArray();

    public ShadowRelation Classify(SphereBound sphere, float margin=0)
    {
        if (!sphere.Valid || !float.IsFinite(margin) || margin < 0) return ShadowRelation.Invalid;
        var distances=Clearances(sphere);
        if (distances.Any(d=>!float.IsFinite(d))) return ShadowRelation.Invalid;
        if (distances.All(d=>d > sphere.Radius+margin)) return ShadowRelation.Inside;
        if (distances.Any(d=>d < -sphere.Radius-margin)) return ShadowRelation.Outside;
        return ShadowRelation.Crossing;
    }
}

/// <summary>Joins the same shape's isolated rasterized depth with the complete unculled scene.
/// Both arrays must come from the same camera, projection, sample layout and scene state.
/// No reference box or centre pixel participates. Counts describe this finite raster only.</summary>
public sealed record ShapeVisibility(int RasterPixels, int VisiblePixels, int HiddenPixels, string? Issue)
{
    public bool Assessed => Issue is null;
    public static ShapeVisibility Compare(ReadOnlySpan<float> shapeDepth, ReadOnlySpan<float> sceneDepth,
                                           float depthTolerance = 0)
    {
        if (shapeDepth.Length == 0 || shapeDepth.Length != sceneDepth.Length ||
            !float.IsFinite(depthTolerance) || depthTolerance < 0)
            return new(0,0,0,"depth arrays or tolerance are invalid");
        int covered=0, visible=0;
        for (int i=0;i<shapeDepth.Length;i++)
        {
            float mine=shapeDepth[i], scene=sceneDepth[i];
            if (!float.IsFinite(mine) || !float.IsFinite(scene) || mine<0 || mine>1 || scene<0 || scene>1)
                return new(covered,visible,covered-visible,"depth contains a non-finite or out-of-range value");
            if (mine == 1) continue;
            covered++;
            if (mine <= scene+depthTolerance) visible++;
        }
        return new(covered,visible,covered-visible,null);
    }
}
