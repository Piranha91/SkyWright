using System.Numerics;
using HarnessShared;
using HousecarlCore;
using SkyWright.Engine;

// THE ROUTE ORACLE'S PER-OBSTACLE DECISIONS (Program.cs's oracle): whether an obstacle is set aside by its base type before
// any measurement, and whether its solid enters an actor column over a walkable (not Deleted-flagged) navmesh triangle at
// all. Held in their own class so --self-test runs exactly the decisions a real run makes; the counters, the floor field
// and the route measurement stay in Program.cs.
static class RouteObstacle
{
    /// <summary>A Door is passable by design (the navmesh runs through the doorway); a MoveableStatic is pushed aside. Both
    /// are set aside, with the reason; everything else is measured.</summary>
    public static bool SetAside(string baseType, out string why)
    {
        if (baseType == "Door") { why = "a Door: passable by design, set aside"; return true; }
        if (baseType == "MoveableStatic") { why = "a MoveableStatic: pushed aside, set aside"; return true; }
        why = "";
        return false;
    }

    /// <summary>The cheap test before the route measurement: does the obstacle's solid (<paramref name="columns"/>) cross
    /// the actor band (step height to <paramref name="top"/>) over any navmesh triangle under <paramref name="query"/>? A
    /// Deleted-flagged triangle (0x08) is no walkable ground.</summary>
    public static bool EntersActorColumn(NavmeshIndex index, MeshColumnIndex columns, Aabb query, float top)
    {
        bool crosses = false;
        foreach (var t in index.Query(query))
        {
            if ((t.Flags & 0x08) != 0) continue;
            var nav = index.NavAt(t.NavIndex); var tri = nav.Triangles[t.TriangleIndex];
            var pa = nav.Vertices[tri.A]; var pb = nav.Vertices[tri.B]; var pc = nav.Vertices[tri.C];
            int x = columns.CountSurfaceCrossings(new Vector3(pa.X, pa.Y, pa.Z), new Vector3(pb.X, pb.Y, pb.Z), new Vector3(pc.X, pc.Y, pc.Z), ActorBand.StepHeight, top, 12f, 1f, 0.5f, stopAfter: 1);
            if (x > 0) { crosses = true; break; }
        }
        return crosses;
    }
}
