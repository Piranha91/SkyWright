using System.Numerics;
using HousecarlCore;
using HousecarlCore.Progress;

// Milestone 4c — SYNTHETIC PROOF for the surface-crossing narrow phase.
//
// NO GAME DATA. Every case is a hand-built mesh whose right answer is known by construction,
// so this can run on a fresh checkout — the shape CONTRIBUTING.md asks proof to take.
//
// It exists because the corpus sweeps cannot tell "the primitive is right and Skyrim is
// noisy" from "the primitive is wrong". A wall must fire, a floor must not, and if those two
// are not nailed down first every percentage that follows is unreadable.

using var __run = RunLog.BeginProcess("crossprobe", args);

int pass = 0, fail = 0;

void Check(string name, bool ok, string detail)
{
    if (ok) { pass++; Console.WriteLine($"   PASS  {name,-52} {detail}"); }
    else { fail++; Console.WriteLine($"   FAIL  {name,-52} {detail}"); }
}

// A closed axis-aligned box as 12 triangles, outward-facing.
static (List<Vector3> V, List<int> I) Box(Vector3 lo, Vector3 hi)
{
    var v = new List<Vector3>
    {
        new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(lo.X, hi.Y, lo.Z),
        new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z),
    };
    var i = new List<int>
    {
        0,2,1, 0,3,2,     // bottom (-Z)
        4,5,6, 4,6,7,     // top    (+Z)
        0,1,5, 0,5,4,     // -Y
        2,3,7, 2,7,6,     // +Y
        1,2,6, 1,6,5,     // +X
        3,0,4, 3,4,7,     // -X
    };
    return (v, i);
}

// A single horizontal quad at height z, as two up-facing triangles.
static (List<Vector3> V, List<int> I) Quad(float x0, float y0, float x1, float y1, float z)
    => (new List<Vector3> { new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z) },
        new List<int> { 0, 1, 2, 0, 2, 3 });

static (List<Vector3> V, List<int> I) Merge(params (List<Vector3> V, List<int> I)[] parts)
{
    var v = new List<Vector3>(); var i = new List<int>();
    foreach (var p in parts) { int b = v.Count; v.AddRange(p.V); foreach (var x in p.I) i.Add(b + x); }
    return (v, i);
}

static MeshColumnIndex Index((List<Vector3> V, List<int> I) m) => MeshColumnIndex.Build(m.V.ToArray(), m.I.ToArray());

// The navmesh triangle used throughout: a 200x200 right triangle lying flat at z=0.
var na = new Vector3(0, 0, 0);
var nb = new Vector3(200, 0, 0);
var nc = new Vector3(0, 200, 0);

int Contained(MeshColumnIndex ix)
{
    int n = 0;
    foreach (var p in new[] { na, nb, nc })
        if (ix.Classify(p) == ColumnPosition.Inside) n++;
    return n;
}

Console.WriteLine("==== surface-crossing narrow phase: synthetic cases ====\n");
Console.WriteLine("   navmesh triangle: (0,0,0) (200,0,0) (0,200,0)   actor height 128\n");

// ---- 1. THE CASE THE OLD RULE COULD NOT SEE ------------------------------------------
// A thin wall standing across the middle of the triangle. Vertex containment sees nothing:
// all three vertices are in open air on either side of it.
{
    var wall = Index(Box(new Vector3(56, -100, 0), new Vector3(64, 300, 200)));
    int cross = wall.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    int cont = Contained(wall);
    Check("thin wall across the triangle -> CROSSES", cross > 0, $"crossings={cross}");
    Check("thin wall -> 0 verts inside", cont == 0, $"verts inside={cont}   <-- the old rule's blind spot");
}

// ---- 2. THE FALSE POSITIVE THE OLD RULE WAS BUILT TO AVOID ---------------------------
{
    var floor = Index(Quad(-100, -100, 400, 400, 0));
    int cross = floor.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    Check("flat floor under the navmesh -> quiet", cross == 0, $"crossings={cross}");

    var slab = Index(Box(new Vector3(-100, -100, -50), new Vector3(400, 400, 0)));
    int sc = slab.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    Check("solid floor slab flush under the navmesh -> quiet", sc == 0, $"crossings={sc}");
}

// ---- 3. CLEARANCE IS WHAT MAKES TERRAIN QUIET ----------------------------------------
// A bump 10 units proud of the navmesh plane — what a faceted rock does under a coarse
// navmesh triangulation. Step height forgives it; no clearance does not.
{
    var bump = Index(Merge(Quad(-100, -100, 400, 400, 0), Box(new Vector3(80, 80, 0), new Vector3(120, 120, 10))));
    int c24 = bump.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    int c2 = bump.CountSurfaceCrossings(na, nb, nc, clearance: 2f, height: 128f, inset: 8f);
    Check("10u bump, clearance 24 -> forgiven", c24 == 0, $"crossings={c24}");
    Check("10u bump, clearance 2  -> flagged", c2 > 0, $"crossings={c2}");
}

// ---- 4. INSET IS WHAT MAKES FLUSH WALLS QUIET ----------------------------------------
// Navmesh is authored right up against walls. A wall whose face is ON the triangle edge is
// BOUNDING the walkable surface, not blocking it.
{
    var flush = Index(Box(new Vector3(-40, -100, 0), new Vector3(0, 300, 200)));   // face exactly at x=0
    int i8 = flush.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    int i0 = flush.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 0f, tolerance: 0f);
    Check("wall flush with the triangle edge, inset 8 -> quiet", i8 == 0, $"crossings={i8}");
    Check("...and exact edge contact is quiet even at inset 0", i0 == 0, $"crossings={i0}  (SAT treats touch as separation)");

    // The case the inset really buys: a wall reaching 3 units PAST the edge. That is a
    // real overlap, so it fires with no inset — and a 3-unit graze is not a blockage.
    var graze = Index(Box(new Vector3(-40, -100, 0), new Vector3(3, 300, 200)));
    int g8 = graze.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    int g0 = graze.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 0f);
    Check("wall 3u past the edge, inset 0 -> fires", g0 > 0, $"crossings={g0}");
    Check("same wall, inset 8 -> forgiven (why inset exists)", g8 == 0, $"crossings={g8}");
}

// ---- 5. THE ROOM SHELL — the case that broke the span test ---------------------------
{
    var room = Merge(
        Quad(-20, -20, 320, 320, 0),                                              // floor
        Quad(-20, -20, 320, 320, 300),                                            // ceiling
        Box(new Vector3(-40, -40, 0), new Vector3(-20, 340, 300)),                // -X wall
        Box(new Vector3(320, -40, 0), new Vector3(340, 340, 300)),                // +X wall
        Box(new Vector3(-40, -40, 0), new Vector3(340, -20, 300)),                // -Y wall
        Box(new Vector3(-40, 320, 0), new Vector3(340, 340, 300)));               // +Y wall
    var ix = Index(room);
    int cross = ix.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    Check("hollow room shell, navmesh on its floor -> quiet", cross == 0, $"crossings={cross}");

    var pillared = Index(Merge(room, Box(new Vector3(60, 60, 0), new Vector3(100, 100, 300))));
    int pc = pillared.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    Check("same room + a pillar on the navmesh -> fires", pc > 0, $"crossings={pc}");
}

// ---- 6. HEADROOM: something ABOVE the actor is not a blocker -------------------------
{
    var arch = Index(Box(new Vector3(-100, -100, 200), new Vector3(400, 400, 240)));
    int cross = arch.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    Check("slab 200u overhead, actor 128u -> quiet", cross == 0, $"crossings={cross}");

    var low = Index(Box(new Vector3(-100, -100, 60), new Vector3(400, 400, 100)));
    int lc = low.CountSurfaceCrossings(na, nb, nc, clearance: 24f, height: 128f, inset: 8f);
    Check("slab 60u overhead, actor 128u -> fires", lc > 0, $"crossings={lc}");
}

// ---- 7. THE STEEPNESS KNOB -----------------------------------------------------------
{
    var low = Index(Box(new Vector3(-100, -100, 60), new Vector3(400, 400, 100)));
    int all = low.CountSurfaceCrossings(na, nb, nc, 24f, 128f, 8f, maxUpFacing: 1f);
    int steep = low.CountSurfaceCrossings(na, nb, nc, 24f, 128f, 8f, maxUpFacing: 0.5f);
    Check("low ceiling counted with maxUpFacing=1", all > 0, $"crossings={all}");
    Check("low ceiling dropped with maxUpFacing=0.5", steep == 0, $"crossings={steep} (its faces are horizontal)");

    var wall = Index(Box(new Vector3(56, -100, 0), new Vector3(64, 300, 200)));
    int ws = wall.CountSurfaceCrossings(na, nb, nc, 24f, 128f, 8f, maxUpFacing: 0.5f);
    Check("wall survives maxUpFacing=0.5", ws > 0, $"crossings={ws}");
}

// ---- 8. UNJUDGEABLE IS NOT CLEAR -----------------------------------------------------
{
    var wall = Index(Box(new Vector3(56, -100, 0), new Vector3(64, 300, 200)));
    int r = wall.CountSurfaceCrossings(new Vector3(0, 0, 0), new Vector3(200, 0, 0), new Vector3(200, 4, 0),
                                       24f, 128f, inset: 8f);
    Check("sliver consumed by the inset -> -1, not 0", r == -1, $"returned={r}");
}

// ---- 9. THE SAT ITSELF, on cases with an answer by inspection ------------------------
{
    bool inside = SpatialOverlap.TriangleIntersectsPrism(
        new Vector3(20, 20, 50), new Vector3(60, 20, 50), new Vector3(20, 60, 50), na, nb, nc, 100f);
    Check("SAT: triangle inside the prism", inside, "");

    bool above = SpatialOverlap.TriangleIntersectsPrism(
        new Vector3(20, 20, 150), new Vector3(60, 20, 150), new Vector3(20, 60, 150), na, nb, nc, 100f);
    Check("SAT: triangle above the prism", !above, "");

    bool beside = SpatialOverlap.TriangleIntersectsPrism(
        new Vector3(300, 300, 50), new Vector3(340, 300, 50), new Vector3(300, 340, 50), na, nb, nc, 100f);
    Check("SAT: triangle beside the prism", !beside, "");

    // Outside the triangle but inside its AABB — the case an AABB test gets wrong.
    bool cornerGap = SpatialOverlap.TriangleIntersectsPrism(
        new Vector3(180, 180, 50), new Vector3(195, 180, 50), new Vector3(180, 195, 50), na, nb, nc, 100f);
    Check("SAT: in the AABB but past the hypotenuse", !cornerGap, "an AABB test would call this a hit");

    bool spear = SpatialOverlap.TriangleIntersectsPrism(
        new Vector3(50, -50, 20), new Vector3(50, 250, 20), new Vector3(50, 100, 90), na, nb, nc, 100f);
    Check("SAT: vertical triangle through the prism", spear, "");
}

// ---- 11. SEVERANCE — the stage that separates a blocker from clutter ------------------
// A crossing test says "object material is in the actor column". Vanilla Skyrim says yes to
// that for 13% of all references, because Bethesda leaves furniture standing on uncut navmesh.
// Severance asks the question that actually distinguishes them: does it CUT the triangle?
{
    // A bigger navmesh triangle, so there is room for both a wall and a shelf.
    var sa = new Vector3(0, 0, 0);
    var sb = new Vector3(400, 0, 0);
    var sc = new Vector3(0, 400, 0);

    var wall = Index(Box(new Vector3(-100, 100, 0), new Vector3(500, 116, 200)));   // spans the whole triangle
    var wr = wall.MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("wall spanning the triangle -> SEVERED", wr.Judged && wr.Severed,
          $"free={wr.Free} blocked={wr.Blocked} comps={wr.Components}");
    Check("...and it crosses too", wall.CountSurfaceCrossings(sa, sb, sc, 24f, 128f, 8f) > 0, "");

    var shelf = Index(Box(new Vector3(40, 40, 0), new Vector3(100, 70, 200)));      // clutter in a corner
    var sr = shelf.MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("shelf standing on the triangle -> NOT severed", sr.Judged && !sr.Severed,
          $"free={sr.Free} blocked={sr.Blocked} comps={sr.Components}");
    Check("...but it DOES cross (why crossing alone over-fires)",
          shelf.CountSurfaceCrossings(sa, sb, sc, 24f, 128f, 8f) > 0, "");

    // A gate leaving a doorway an actor can walk through is not a blockage.
    var gate = Index(Merge(Box(new Vector3(-100, 100, 0), new Vector3(150, 116, 200)),
                           Box(new Vector3(270, 100, 0), new Vector3(500, 116, 200))));
    var gr = gate.MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("wall with a 120u gap -> NOT severed", gr.Judged && !gr.Severed,
          $"free={gr.Free} blocked={gr.Blocked} comps={gr.Components}");

    // Close the gap below actor width and it is a blockage again.
    var pinch = Index(Merge(Box(new Vector3(-100, 100, 0), new Vector3(196, 116, 200)),
                            Box(new Vector3(212, 100, 0), new Vector3(500, 116, 200))));
    var pr = pinch.MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("same wall with a 16u gap -> SEVERED", pr.Judged && pr.Severed,
          $"free={pr.Free} blocked={pr.Blocked} comps={pr.Components}");

    // The floor the navmesh lies on must stay silent here too.
    var floorS = Index(Quad(-100, -100, 600, 600, 0));
    var fr = floorS.MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("flat floor -> NOT severed", fr.Judged && !fr.Severed, $"blocked={fr.Blocked}");

    // A hollow room shell whose walls BOUND the triangle rather than block it.
    var roomS = Merge(
        Quad(-20, -20, 420, 420, 0),
        Box(new Vector3(-40, -40, 0), new Vector3(-20, 440, 300)),
        Box(new Vector3(420, -40, 0), new Vector3(440, 440, 300)),
        Box(new Vector3(-40, -40, 0), new Vector3(440, -20, 300)),
        Box(new Vector3(-40, 420, 0), new Vector3(440, 440, 300)));
    var rr = Index(roomS).MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("room shell around the triangle -> NOT severed", rr.Judged && !rr.Severed, $"blocked={rr.Blocked}");

    // A triangle the inset cannot survive is UNJUDGED, not clear.
    var un = wall.MeasureSeverance(new Vector3(0, 0, 0), new Vector3(400, 0, 0), new Vector3(400, 6, 0), 24f, 128f, 8f, 32f);
    Check("sliver triangle -> unjudged, not clear", !un.Judged, $"judged={un.Judged} severed={un.Severed}");
}

// ---- 12. SUPPORT — telling the ground from an obstacle standing on it -----------------
// The residue after severance is landscape: mountain trim, rock cliffs, rock piles. Those are
// not obstacles ON the navmesh, they ARE the ground the navmesh was draped over. The signal is
// whether the object HOLDS UP the walkable surface.
{
    var sa = new Vector3(0, 0, 0);
    var sb = new Vector3(400, 0, 0);
    var sc = new Vector3(0, 400, 0);

    var ground = Index(Quad(-100, -100, 600, 600, 0));
    var gr = ground.MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("flat ground under the navmesh -> fully supported", gr.SupportedFraction > 0.95f,
          $"supported={gr.SupportedFraction:0.00}");

    var wall = Index(Box(new Vector3(-100, 100, 0), new Vector3(500, 116, 200)));
    var wr = wall.MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("wall severing the navmesh -> barely supports it", wr.Severed && wr.SupportedFraction < 0.3f,
          $"supported={wr.SupportedFraction:0.00} severed={wr.Severed}");

    // A boulder the navmesh runs over: its top surface IS the walkable plane across most of the
    // triangle, and it pokes up in the middle. Severed, but supported — the terrain case.
    var mound = Index(Merge(Quad(-100, -100, 600, 600, 0),
                            Box(new Vector3(-100, 100, 0), new Vector3(500, 116, 60))));
    var mr = mound.MeasureSeverance(sa, sb, sc, 24f, 128f, 8f, 32f);
    Check("ridge on ground the navmesh lies on -> supported", mr.SupportedFraction > 0.7f,
          $"supported={mr.SupportedFraction:0.00} severed={mr.Severed}");
}

// ---- 10. INSET GEOMETRY --------------------------------------------------------------
{
    bool ok = SpatialOverlap.InsetTriangle(na, nb, nc, 10f, out var a2, out _, out _);
    float r = 58.5786f;                                   // inradius of a 200/200/282.84 right triangle
    float expect = (r - 10f) / r;
    float la = (nb - nc).Length(), lb = (nc - na).Length(), lc = (na - nb).Length();
    var inc = (na * la + nb * lb + nc * lc) / (la + lb + lc);
    float got = (a2 - inc).Length() / (na - inc).Length();
    Check("InsetTriangle scales about the incentre", ok && MathF.Abs(got - expect) < 1e-3f,
          $"scale={got:0.0000} expected={expect:0.0000}");

    bool over = SpatialOverlap.InsetTriangle(na, nb, nc, 100f, out _, out _, out _);
    Check("InsetTriangle refuses an inset past the inradius", !over, "inset 100 > inradius 58.6");
}

// ---- 13. THE WORLD TRANSFORM — the engine's Euler composition, pinned -------------------
// SpatialOverlap.ToWorld places a model box with RefrRotation: Rx(-x)*Ry(-y)*Rz(-z), derived from the
// engine's NiMatrix3::SetEulerAnglesXYZ and confirmed in game on tilted architecture. It is the TRANSPOSE
// of the Rz*Ry*Rx composition the detectors carried until the re-baseline — a different SIGN on every
// angle and a different ORDER. Expected boxes are hand-computed (numpy, column-vector rotations), not
// read back from the code. Four cases, each proving one thing:
//   A  a CENTRED box under a single yaw agrees under both compositions — order cannot matter with one
//      axis, and mirroring a centred box gives the same AABB. Must stay green under either composition.
//   B  a centred box under two axes: same AABB under any single axis, different under the transpose —
//      proves the ORDER.
//   C  an OFF-CENTRE box under a plain yaw lands on the opposite side of the ref under the wrong sign —
//      proves the SIGN, on the case nearly every static in the game is (yaw-only).
//   D  three axes, off-centre, scale 2 — the general case.
{
    static bool Near(Aabb got, float x0, float y0, float z0, float x1, float y1, float z1, float tol = 0.02f) =>
        !got.IsEmpty
        && MathF.Abs(got.Min.X - x0) < tol && MathF.Abs(got.Min.Y - y0) < tol && MathF.Abs(got.Min.Z - z0) < tol
        && MathF.Abs(got.Max.X - x1) < tol && MathF.Abs(got.Max.Y - y1) < tol && MathF.Abs(got.Max.Z - z1) < tol;
    static string Show(Aabb b) => b.IsEmpty ? "<empty>" : $"({b.Min.X:0.00},{b.Min.Y:0.00},{b.Min.Z:0.00})..({b.Max.X:0.00},{b.Max.Y:0.00},{b.Max.Z:0.00})";
    const float Deg = MathF.PI / 180f;
    var centred = new Aabb(new Vec3(-100, -20, -50), new Vec3(100, 20, 50), false);
    var offCentre = new Aabb(new Vec3(0, -20, 0), new Vec3(200, 20, 100), false);

    var a = SpatialOverlap.ToWorld(centred, new Vec3(1000, 2000, 300), new Vec3(0, 0, 30 * Deg), 1f);
    Check("ToWorld A: centred box, yaw 30 -> hand-computed box", Near(a, 903.3975f, 1932.6795f, 250f, 1096.6025f, 2067.3205f, 350f), Show(a));
    // ...and the OLD composition gives the same box here, which is why a yaw-only calibration could never see the bug.
    var aOld = Aabb.Empty;
    var mOld = SpatialOverlap.EulerZyx(new Vec3(0, 0, 30 * Deg));
    for (int i = 0; i < 8; i++)
        aOld = aOld.Add(Vector3.Transform(new Vector3((i & 1) == 0 ? -100 : 100, (i & 2) == 0 ? -20 : 20, (i & 4) == 0 ? -50 : 50), mOld) + new Vector3(1000, 2000, 300));
    Check("ToWorld A: ...and Rz*Ry*Rx un-negated agrees on it (centred + one axis hides sign AND order)",
          Near(aOld, 903.3975f, 1932.6795f, 250f, 1096.6025f, 2067.3205f, 350f), Show(aOld));

    var b = SpatialOverlap.ToWorld(centred, new Vec3(1000, 2000, 300), new Vec3(40 * Deg, 0, 30 * Deg), 1f);
    Check("ToWorld B: centred box, x 40 + yaw 30 -> the ORDER (transpose gives 889.67..1110.33 in X)",
          Near(b, 903.3975f, 1916.2901f, 218.425f, 1096.6025f, 2083.7099f, 381.575f), Show(b));

    var c = SpatialOverlap.ToWorld(offCentre, new Vec3(0, 0, 0), new Vec3(0, 0, 90 * Deg), 1f);
    Check("ToWorld C: off-centre box, yaw 90 -> the SIGN (heading 90 = clockwise; +X model axis lands on -Y)",
          Near(c, -20f, -200f, 0f, 20f, 0f, 100f), Show(c));

    var d = SpatialOverlap.ToWorld(offCentre, new Vec3(500, -300, 40), new Vec3(20 * Deg, -35 * Deg, 110 * Deg), 2f);
    Check("ToWorld D: three axes, off-centre, scale 2 -> hand-computed box",
          Near(d, 357.1434f, -646.6001f, 24.4199f, 645.5053f, -223.7372f, 411.8254f), Show(d));
}

// ---- 15. TRIANGLE-vs-BOX on NON-CUBIC boxes -------------------------------------------------------------
// Found 2026-09-02 when the route raster's thin column query returned no ground: the nine edge-cross axis
// tests paired each edge component with the WRONG box half-extent, which is exact only when the box is a cube.
// A tall thin column over a flat floor and a wide flat slab pierced by a vertical triangle are the two shapes
// that expose it; the cases here are the ones that were RED before the fix.
{
    // A 1 x 1 x 712 column standing on a flat floor triangle: must intersect.
    var column = new Aabb(new Vec3(-0.5f, 63.5f, -256), new Vec3(0.5f, 64.5f, 456), false);
    bool col = SpatialOverlap.TriangleIntersectsBox(new Vector3(-600, 0, 0), new Vector3(600, 0, 0), new Vector3(600, 128, 0), column);
    Check("SAT box: thin tall column over a flat floor -> intersects", col, "was RED: edge-cross radius used the wrong half-extents");

    // A wide flat slab pierced by a vertical triangle 10 u off its centre: must intersect.
    var slab = new Aabb(new Vec3(-400, -400, -0.5f), new Vec3(400, 400, 0.5f), false);
    bool pierced = SpatialOverlap.TriangleIntersectsBox(new Vector3(0, 10, -20), new Vector3(0, 10, 20), new Vector3(30, 10, 0), slab);
    Check("SAT box: wide flat slab pierced by a vertical triangle -> intersects", pierced, "");

    // The severance column (32 x 32 x 96) against a wall face whose TOP edge sits 20 u above the column centre:
    // the face crosses the column and must intersect. The bug rejected it on the top edge's axis.
    var cell = new Aabb(new Vec3(0, 0, 32), new Vec3(32, 32, 128), false);
    bool wallTop = SpatialOverlap.TriangleIntersectsBox(new Vector3(-100, 16, 0), new Vector3(100, 16, 0), new Vector3(100, 16, 100), cell);
    Check("SAT box: actor column vs a wall face topping out inside it -> intersects", wallTop, "");

    // ...and a triangle genuinely beside the column stays separated.
    bool beside = SpatialOverlap.TriangleIntersectsBox(new Vector3(40, -100, 0), new Vector3(40, 100, 0), new Vector3(40, 100, 300), cell);
    Check("SAT box: wall face 8 u past the column -> separated", !beside, "");
}

// ---- 14. THE ROUTE — with vs without, on a synthetic corridor (roadmap 4.7 Part B) --------------------
// A navmesh strip 128 u wide (Y 3..131 — deliberately OFF the 16 u raster grid, the way real ground is; a strip
// at Y 0..128 puts cell centres exactly on its edges and the raster reads those as ground) and 1,200 u long, two
// triangles. Obstacles are hand-built boxes; the answers are known by construction. Ladder 0/32/64/96/128,
// margin 256, step 16, band 32..128. The raster's resolution is half a cell — 8 u — on every edge.
{
    var strip = new NavmeshGeometry(Mutagen.Bethesda.Plugins.FormKey.Null,
        new[] { new Vec3(-600, 3, 0), new Vec3(600, 3, 0), new Vec3(600, 131, 0), new Vec3(-600, 131, 0) },
        new[] { new NavTri(0, 1, 2, 0), new NavTri(0, 2, 3, 0) },
        new Vec3(-600, 3, 0), new Vec3(600, 131, 0));
    var navs = NavmeshIndex.Build(new[] { strip }, out _);
    var ladder = new[] { 0f, 32f, 64f, 96f, 128f };
    static Aabb BoxOf(Vector3 lo, Vector3 hi) => new(new Vec3(lo.X, lo.Y, lo.Z), new Vec3(hi.X, hi.Y, hi.Z), false);
    RouteFacts Route(Vector3 lo, Vector3 hi) =>
        RouteConnectivity.Measure(navs, Index(Box(lo, hi)), BoxOf(lo, hi), margin: 256f, step: 16f, clearance: 32f, height: 128f, ladder);
    static string Show(RouteFacts f) => $"judged={f.Judged} touched={f.Touched} intact={f.MaxIntactRung} without={f.MaxIntactRungWithout} groups={f.BoundaryGroups} {f.Why}";

    // The corridor itself, no obstacle on it: a shelf beside the strip, off the navmesh.
    var beside = Route(new Vector3(-20, 200, 0), new Vector3(20, 260, 100));
    Check("route: object beside the corridor -> touched 0", beside.Judged && beside.Touched == 0, Show(beside));
    Check("route: ...and the corridor passes 96 u without it, not 128", beside.MaxIntactRungWithout == 3, Show(beside));

    // The canonical failure: a wall across the whole corridor.
    var wall = Route(new Vector3(0, -20, 0), new Vector3(16, 148, 200));
    Check("route: wall across the corridor -> split at rung 0 (BLOCKED)", wall.Judged && wall.Touched > 0 && wall.MaxIntactRung < 0, Show(wall));
    Check("route: ...while the corridor was one piece without it", !wall.SplitAtWithout[0] && wall.BoundaryGroups == 1, Show(wall));

    // A wall leaving a 61 u gap (Y 3..64): a route survives at 32 u clearance and fails at 64.
    var gap = Route(new Vector3(0, 64, 0), new Vector3(16, 148, 200));
    Check("route: wall with a 61 u gap -> intact at 32, split at 64", gap.Judged && gap.Touched > 0 && gap.MaxIntactRung == 1, Show(gap));

    // Clutter in a corner: ~88 u of corridor left — intact at 64, not at 96.
    var shelf = Route(new Vector3(0, 0, 0), new Vector3(40, 40, 200));
    Check("route: shelf in the corner -> intact at 64, split at 96", shelf.Judged && shelf.Touched > 0 && shelf.MaxIntactRung == 2, Show(shelf));

    // A wall flush along the corridor's edge (its face 3 u short of the ground) bounds it, it does not block it.
    var flush = Route(new Vector3(-100, -40, 0), new Vector3(100, 0, 200));
    Check("route: wall flush along the edge -> touched 0", flush.Judged && flush.Touched == 0, Show(flush));

    // A bridge deck over the corridor, above the band.
    var bridge = Route(new Vector3(-100, -20, 160), new Vector3(100, 148, 200));
    Check("route: bridge 160 u overhead -> touched 0", bridge.Judged && bridge.Touched == 0, Show(bridge));

    // Nothing to walk on: an object far from any navmesh is UNJUDGED, not clear.
    var far = Route(new Vector3(5000, 5000, 0), new Vector3(5016, 5128, 200));
    Check("route: no ground in the window -> unjudged", !far.Judged, Show(far));

    // A city wall whose collision reaches 800 u BELOW the street (Whiterun's do): the street is still the storey
    // that gets judged, and the wall still blocks it.
    var deep = Route(new Vector3(0, -20, -800), new Vector3(16, 148, 200));
    Check("route: wall with an 800 u foundation -> judged on the street, BLOCKED", deep.Judged && deep.MaxIntactRung < 0 && deep.Storeys == 1, Show(deep) + $" storeys={deep.Storeys} z={deep.StoreyZ:0}");

    // Two storeys: a second strip 300 u above the first. A wall across only the UPPER one is out of the street's
    // reach (a floor 300 u below cannot put an actor into it), so one storey is measured and it is the upper, blocked.
    var upper = new NavmeshGeometry(Mutagen.Bethesda.Plugins.FormKey.Null,
        new[] { new Vec3(-600, 3, 300), new Vec3(600, 3, 300), new Vec3(600, 131, 300), new Vec3(-600, 131, 300) },
        new[] { new NavTri(0, 1, 2, 0), new NavTri(0, 2, 3, 0) },
        new Vec3(-600, 3, 300), new Vec3(600, 131, 300));
    var two = NavmeshIndex.Build(new[] { strip, upper }, out _);
    var upperWallLo = new Vector3(0, -20, 300); var upperWallHi = new Vector3(16, 148, 500);
    var twoFacts = RouteConnectivity.Measure(two, Index(Box(upperWallLo, upperWallHi)), BoxOf(upperWallLo, upperWallHi), 256f, 16f, 32f, 128f, ladder);
    Check("route: two storeys, wall across the upper only -> the upper is the storey judged, BLOCKED", twoFacts.Judged && twoFacts.Storeys == 1 && twoFacts.MaxIntactRung < 0 && twoFacts.StoreyZ > 200, Show(twoFacts) + $" storeys={twoFacts.Storeys} z={twoFacts.StoreyZ:0}");

    // ...and a wall rising from the street through both storeys is measured on both and blocks both.
    var tallLo = new Vector3(0, -20, 0); var tallHi = new Vector3(16, 148, 500);
    var tallFacts = RouteConnectivity.Measure(two, Index(Box(tallLo, tallHi)), BoxOf(tallLo, tallHi), 256f, 16f, 32f, 128f, ladder);
    Check("route: two storeys, wall through both -> 2 storeys measured, BLOCKED", tallFacts.Judged && tallFacts.Storeys == 2 && tallFacts.MaxIntactRung < 0, Show(tallFacts) + $" storeys={tallFacts.Storeys} z={tallFacts.StoreyZ:0}");
}

// ---- 16. THE CAPSULE SWEEP PRIMITIVE — a disc in a height slab against the solid ------------------------------
// The route-cut rule's cut test is MeshColumnIndex.IntersectsDiscInSlab: exact, so the margin Part C measured ("the
// line clears the block by 1 u; straight through") is a margin the primitive can see.
{
    var block = Index(Box(new Vector3(0, 0, 0), new Vector3(64, 64, 84)));     // NightingaleBlock01's shape
    Check("disc: face 1 u outside the disc -> no contact", !block.IntersectsDiscInSlab(-23, 32, 22f, 32, 112), "disc centre 23 u from the face, radius 22");
    Check("disc: face 1 u inside the disc -> contact", block.IntersectsDiscInSlab(-21, 32, 22f, 32, 112), "disc centre 21 u from the face, radius 22");
    Check("disc: centre inside the block -> contact", block.IntersectsDiscInSlab(32, 32, 22f, 32, 112), "");
    Check("disc: corner 1 u inside the disc, diagonally -> contact", block.IntersectsDiscInSlab(-14.85f, -14.85f, 22f, 32, 112), "the corner is 21 u away along the diagonal");
    Check("disc: corner 1 u outside the disc, diagonally -> no contact", !block.IntersectsDiscInSlab(-16.27f, -16.27f, 22f, 32, 112), "the corner is 23 u away along the diagonal");

    var beam = Index(Box(new Vector3(-20, -100, 100), new Vector3(2, 300, 122)));   // a beam whose underside is at 100
    Check("disc: beam bottom 100 in a band to 112 -> contact", beam.IntersectsDiscInSlab(0, 50, 22f, 32, 112), "");
    Check("disc: the same beam in a band to 99 -> no contact", !beam.IntersectsDiscInSlab(0, 50, 22f, 32, 99), "");
    var high = Index(Box(new Vector3(-20, -100, 140), new Vector3(2, 300, 162)));
    Check("disc: beam bottom 140 in a band to 112 -> no contact", !high.IntersectsDiscInSlab(0, 50, 22f, 32, 112), "");

    // A wall seen edge-on — its face projects to a segment with no plan area — still counts by distance.
    var thin = Index((new List<Vector3> { new(10, -100, 0), new(10, 100, 0), new(10, 100, 200) }, new List<int> { 0, 1, 2 }));
    Check("disc: a single vertical face 10 u away -> contact", thin.IntersectsDiscInSlab(0, 0, 22f, 32, 112), "");
    Check("disc: ...and 30 u away -> no contact", !thin.IntersectsDiscInSlab(-20, 0, 22f, 32, 112), "");
}

// ---- 17. THE ROUTE-CUT LINE — the corridor's path line, the sweep, the passage (roadmap 4.7, the rule Part C returned)
// The same synthetic strip, now with its two triangles LINKED as a navmesh record would link them (the graph reads the
// record's edge links, never shared vertices), so NavGraph can walk it. Band 32..112: the Part C walker's nominal 132 u
// × the measured 0.85. Capsule 22 u. The raster's ends are the boundary runs' middles (the midpoint of the two middle
// cells of an even run), so on this strip (8 cells across, centres at Y 11, 27, …, 123) the line runs at Y 67 — the
// strip's true middle. A run with an odd count would put the end on a cell centre, up to 8 u off: the raster's
// resolution, stated rather than hidden.
{
    static NavmeshGeometry Strip(uint id, float x0, float x1, float y0, float y1, float z) =>
        new(new Mutagen.Bethesda.Plugins.FormKey(Mutagen.Bethesda.Plugins.ModKey.FromNameAndExtension("synthetic.esp"), id),
            new[] { new Vec3(x0, y0, z), new Vec3(x1, y0, z), new Vec3(x1, y1, z), new Vec3(x0, y1, z) },
            new[] { new NavTri(0, 1, 2, 0, -1, -1, 1), new NavTri(0, 2, 3, 0, 0, -1, -1) },
            new Vec3(x0, y0, z), new Vec3(x1, y1, z), Array.Empty<NavEdgeLink>(), Array.Empty<NavDoorTriangle>());
    static Aabb BoxOf(Vector3 lo, Vector3 hi) => new(new Vec3(lo.X, lo.Y, lo.Z), new Vec3(hi.X, hi.Y, hi.Z), false);
    var ladder = new[] { 0f, 32f, 64f, 96f, 128f };
    var corridor = NavmeshIndex.Build(new[] { Strip(1, -600, 600, 3, 131, 0) }, out _);
    RouteFacts Cut(NavmeshIndex navs, Vector3 lo, Vector3 hi, float top = 112f, Vector2[]? line = null) =>
        RouteConnectivity.Measure(navs, Index(Box(lo, hi)), BoxOf(lo, hi), 256f, 16f, 32f, top, ladder, lineOverride: line);
    static string ShowLine(RouteFacts f) => f.Line is { } l
        ? $"touched={f.Touched} {l.Status} ends={l.Ends} pairs={l.PairsCut}/{l.Pairs} pts={l.Points.Length} cut={l.Cut} passage={l.Passage:0} (L {l.PassageLeft:0} / R {l.PassageRight:0}) {l.Why}"
        : $"judged={f.Judged} {f.Why}";

    // A block ON the line: 64 u across, centred, 32 u left each side. Part C: the centred 64 block stalled 2 of 2.
    var on = Cut(corridor, new Vector3(0, 35, 0), new Vector3(64, 99, 84));
    Check("cut: block on the line -> a line with two ends, straight", on.Line is { Status: LineStatus.Line, Ends: 2, Points.Length: 2 }, ShowLine(on));
    Check("cut: ...the line runs down the corridor's middle, Y 67", on.Line is { } l0 && MathF.Abs(l0.Points[0].Y - 67f) < 0.5f && MathF.Abs(l0.Points[1].Y - 67f) < 0.5f
          && MathF.Min(l0.Points[0].X, l0.Points[1].X) < -200 && MathF.Max(l0.Points[0].X, l0.Points[1].X) > 300,
          ShowLine(on) + (on.Line is { Points.Length: 2 } lp ? $" ({lp.Points[0].X:0},{lp.Points[0].Y:0})->({lp.Points[1].X:0},{lp.Points[1].Y:0})" : ""));
    Check("cut: ...is CUT, with 32 u open beside it", on.Line is { Cut: true } l1 && MathF.Abs(l1.Passage - 32f) < 0.5f, ShowLine(on));
    Check("cut: ...and both sides read 32", on.Line is { } l2 && MathF.Abs(l2.PassageLeft - 32f) < 0.5f && MathF.Abs(l2.PassageRight - 32f) < 0.5f, ShowLine(on));

    // A block BESIDE the line: from the left edge to Y 40; the line at 67 clears it by 5 u past the capsule (45).
    var beside = Cut(corridor, new Vector3(0, 3, 0), new Vector3(64, 40, 84));
    Check("cut: block beside the line -> on ground, line found, NOT cut", beside.Touched > 0 && beside.Line is { Status: LineStatus.Line, Cut: false }, ShowLine(beside));

    // The same block to Y 44: the capsule (reaching 45) misses it by 1 u; to Y 46 it reaches it by 1 u.
    var miss = Cut(corridor, new Vector3(0, 3, 0), new Vector3(64, 44, 84));
    Check("cut: block 1 u short of the capsule -> not cut", miss.Line is { Cut: false }, ShowLine(miss));
    var graze = Cut(corridor, new Vector3(0, 3, 0), new Vector3(64, 46, 84));
    Check("cut: block reaching 1 u into the capsule -> cut", graze.Line is { Cut: true }, ShowLine(graze));

    // The full-width wall: cut with nothing beside it.
    var wall = Cut(corridor, new Vector3(0, -20, 0), new Vector3(16, 148, 200));
    Check("cut: full-width wall -> cut, passage 0", wall.Line is { Cut: true, Passage: 0f }, ShowLine(wall));

    // A block from the left edge to Y 60: cut (the capsule reaches 53), and 64 u open on the right — the coin.
    var coin = Cut(corridor, new Vector3(0, 3, 0), new Vector3(64, 60, 84));
    Check("cut: block leaving 64 u on the right -> cut, passage 64 on the right, 0 on the left", coin.Line is { Cut: true } l3 && MathF.Abs(l3.Passage - 64f) < 0.5f && l3.PassageRight < 1f && MathF.Abs(l3.PassageLeft - 64f) < 0.5f || coin.Line is { Cut: true } l4 && MathF.Abs(l4.Passage - 64f) < 0.5f && l4.PassageLeft < 1f && MathF.Abs(l4.PassageRight - 64f) < 0.5f, ShowLine(coin));

    // The overhead ladder, as Part C ran it: a beam across the corridor with its underside at 100, 120, 140 u.
    var beam100 = Cut(corridor, new Vector3(0, -20, 100), new Vector3(22, 148, 122));
    Check("cut: beam at 100 u, band to 112 -> cut, passage 0 (Part C: stalled)", beam100.Touched > 0 && beam100.Line is { Cut: true, Passage: 0f }, ShowLine(beam100));
    var beam120 = Cut(corridor, new Vector3(0, -20, 120), new Vector3(22, 148, 142));
    Check("cut: beam at 120 u, band to 112 -> touches nothing (Part C: passed under)", beam120.Judged && beam120.Touched == 0, ShowLine(beam120));
    var beam140 = Cut(corridor, new Vector3(0, -20, 140), new Vector3(22, 148, 162));
    Check("cut: beam at 140 u, band to 112 -> touches nothing (Part C: no reaction)", beam140.Judged && beam140.Touched == 0, ShowLine(beam140));
    // ...and the one arm the band factor decides: at the NOMINAL 132 u the 120 beam is a cut. Stated, not claimed right.
    var beam120n = Cut(corridor, new Vector3(0, -20, 120), new Vector3(22, 148, 142), top: 132f);
    Check("cut: beam at 120 u at the nominal 132 u band -> cut (the band factor's one deciding arm)", beam120n.Line is { Cut: true }, ShowLine(beam120n));

    // A caller-supplied line: the corridor's true middle (Y 67) instead of the raster's.
    var supplied = Cut(corridor, new Vector3(0, 3, 0), new Vector3(64, 40, 84), line: new[] { new Vector2(-300, 67), new Vector2(300, 67) });
    Check("cut: supplied line at Y 67 past a block to Y 40 -> Supplied, not cut (clears by 5)", supplied.Line is { Status: LineStatus.Supplied, Cut: false }, ShowLine(supplied));
    var supplied2 = Cut(corridor, new Vector3(0, 3, 0), new Vector3(64, 46, 84), line: new[] { new Vector2(-300, 67), new Vector2(300, 67) });
    Check("cut: supplied line at Y 67 past a block to Y 46 -> cut (reaches by 1)", supplied2.Line is { Status: LineStatus.Supplied, Cut: true }, ShowLine(supplied2));

    // NO LINE, said so. A dead end: the strip stops inside the window.
    var deadEnd = NavmeshIndex.Build(new[] { Strip(2, -600, 100, 3, 131, 0) }, out _);
    var de = Cut(deadEnd, new Vector3(0, 35, 0), new Vector3(64, 99, 84));
    Check("cut: corridor ends inside the window -> DeadEnd, no line", de.Touched > 0 && de.Line is { Status: LineStatus.DeadEnd, Found: false }, ShowLine(de));

    // A junction: a second strip crossing the first. Four ends; the two strips are separate records with no links
    // between them, so of the six pairs only W–E and N–S connect. A block at the crossing cuts both lines: the worst
    // is reported, and the status says a junction was generalised. (The Ratway station Part C measured is a T within
    // the window's reach — 27 of its 34 arms had no line until junctions were measured pairwise.)
    var junction = NavmeshIndex.Build(new[] { Strip(3, -600, 600, 3, 131, 0), Strip(4, 3, 131, -600, 600, 0) }, out _);
    var jn = Cut(junction, new Vector3(35, 35, 0), new Vector3(99, 99, 84));
    // The passage beside a block AT a crossing is the 32 u lane along the arm (two cells, each next to the block or the
    // edge); the cross-section THROUGH the crossing reads 45 on each side (the free cell there has ground diagonally
    // into the other arm), which is why the per-side figure is a diagnostic and the bottleneck is the number.
    Check("cut: block at a crossing -> JunctionLine, 4 ends, 2 pairs connected, both cut, passage 32 (the side lanes read 45)",
          jn.Touched > 0 && jn.Line is { Status: LineStatus.JunctionLine, Ends: 4, Pairs: 2, PairsCut: 2, Cut: true } lj && MathF.Abs(lj.Passage - 32f) < 0.5f && MathF.Abs(lj.PassageLeft - 45.25f) < 0.5f, ShowLine(jn));
    // ...and a block on the EAST arm only: the W–E line is cut, the N–S line is not; the worst (cut) is reported.
    var jnEast = Cut(junction, new Vector3(200, 35, 0), new Vector3(264, 99, 84));
    Check("cut: block on one arm of a crossing -> JunctionLine, 1 of 2 pairs cut, reported cut",
          jnEast.Line is { Status: LineStatus.JunctionLine, Pairs: 2, PairsCut: 1, Cut: true }, ShowLine(jnEast));
    // ...and a block beside the east arm's line: no pair is cut -> Clipped-shaped facts.
    var jnBeside = Cut(junction, new Vector3(200, 3, 0), new Vector3(264, 40, 84));
    Check("cut: block beside one arm of a crossing -> JunctionLine, 0 pairs cut", jnBeside.Touched > 0 && jnBeside.Line is { Status: LineStatus.JunctionLine, PairsCut: 0, Cut: false }, ShowLine(jnBeside));

    // Open ground: navmesh all round the window.
    var field = NavmeshIndex.Build(new[] { Strip(5, -1000, 1000, -1000, 1000, 0) }, out _);
    var og = Cut(field, new Vector3(0, 0, 0), new Vector3(64, 64, 84));
    Check("cut: a block in an open field -> OpenGround, no line", og.Touched > 0 && og.Line is { Status: LineStatus.OpenGround }, ShowLine(og));

    // The record's links are the graph: the same corridor with its two triangles UNLINKED has a raster that connects
    // the ends and a record that does not — NoPath, never a line drawn through geometry the game does not walk.
    var unlinked = NavmeshIndex.Build(new[] { new NavmeshGeometry(new Mutagen.Bethesda.Plugins.FormKey(Mutagen.Bethesda.Plugins.ModKey.FromNameAndExtension("synthetic.esp"), 6),
        new[] { new Vec3(-600, 3, 0), new Vec3(600, 3, 0), new Vec3(600, 131, 0), new Vec3(-600, 131, 0) },
        new[] { new NavTri(0, 1, 2, 0), new NavTri(0, 2, 3, 0) }, new Vec3(-600, 3, 0), new Vec3(600, 131, 0)) }, out _);
    var np = Cut(unlinked, new Vector3(0, 35, 0), new Vector3(64, 99, 84));
    Check("cut: the same corridor with unlinked triangles -> NoPath, no line", np.Touched > 0 && np.Line is { Status: LineStatus.NoPath }, ShowLine(np));

    // The passage counts only ground that gets PAST the object. A wall across the corridor with a dead-end room beside
    // it — reached through a neck on the near side, 200 u wide, no other way out — reads a 128 u "lane" on the cross
    // section through the room, and that lane leads nowhere: with the object in place the two ends are on different
    // free components, so the passage is 0. (A city gate piece read a wide lane beside it before this.)
    var pocket = NavmeshIndex.Build(new[] { Strip(7, -600, 600, 3, 131, 0), Strip(8, -100, 0, 131, 195, 0), Strip(9, -100, 100, 195, 400, 0) }, out _);
    var pk = Cut(pocket, new Vector3(0, -20, 0), new Vector3(16, 195, 200));
    Check("cut: wall across the corridor with a dead-end room beside it -> cut, passage 0 (the room leads nowhere)",
          pk.Line is { Cut: true, Passage: 0f }, ShowLine(pk));
    // ...and the same room with a second neck on the FAR side is a way round — and the passage is the way round's
    // BOTTLENECK, the 64 u necks, not the 200 u room the cross-section through the wall sees (a city gate piece read
    // the open ground beside it as its passage while the way past it was a squeeze elsewhere in the window).
    var bypass = NavmeshIndex.Build(new[] { Strip(10, -600, 600, 3, 131, 0), Strip(11, -100, 0, 131, 195, 0), Strip(12, -100, 100, 195, 400, 0), Strip(13, 36, 100, 131, 195, 0) }, out _);
    var bp = Cut(bypass, new Vector3(0, -20, 0), new Vector3(16, 195, 200));
    Check("cut: ...the same room with a neck on the far side too -> cut, passage 64 (the necks, the bottleneck of the way round)",
          bp.Line is { Cut: true } lbp && MathF.Abs(lbp.Passage - 64f) < 0.5f, ShowLine(bp));
    // ...and with the far neck squeezed to 32 u across (X 36..68) the same way round is a 32 u passage: BLOCKED-range.
    var squeeze = NavmeshIndex.Build(new[] { Strip(14, -600, 600, 3, 131, 0), Strip(15, -100, 0, 131, 195, 0), Strip(16, -100, 100, 195, 400, 0), Strip(17, 36, 68, 131, 195, 0) }, out _);
    var sq = Cut(squeeze, new Vector3(0, -20, 0), new Vector3(16, 195, 200));
    Check("cut: ...the far neck squeezed to 32 u -> passage 32", sq.Line is { Cut: true } lsq && MathF.Abs(lsq.Passage - 32f) < 0.5f, ShowLine(sq));

    // An object touching nothing asks for no line.
    var over = Cut(corridor, new Vector3(-100, -20, 160), new Vector3(100, 148, 200));
    Check("cut: bridge above the band -> line not asked", over.Touched == 0 && over.Line is { Status: LineStatus.NotAsked }, ShowLine(over));
}

// ---- 18. THE CONTACT PRIMITIVE — soup against soup (milestone 6; the test roadmap 4.5 / 4.6 run on) -----------------
// SpatialOverlap.Contact: a dependent soup against a host soup, both in world space — the least surface distance
// (0 on touch or crossing), the deepest a dependent vertex sits inside the host, and the host's normal at the
// contact. Every expected value is known by construction; nothing here reads a NIF.
{
    static TriangleSoup Soup((List<Vector3> V, List<int> I) m) => new(m.V.ToArray(), m.I.ToArray());
    static string Show(SoupContact c) => $"dist={c.Distance:0.###} facing={c.FacingDistance:0.###} pen={c.Penetration:0.###} enc={c.EnclosedDepth:0.###}{(c.HostClosed ? "(closed)" : "(open)")} up={c.NormalUp:0.###} n=({c.Normal.X:0.##},{c.Normal.Y:0.##},{c.Normal.Z:0.##}) pairs={c.ContactPairs} through={c.PenetratingVertices} inside={c.EnclosedVertices}";
    var table = Soup(Box(new Vector3(-100, -100, 0), new Vector3(100, 100, 64)));      // a 200 x 200 x 64 host, top at z = 64

    // A. resting on the top: distance 0, no penetration, the host's normal is +Z.
    var apple = Soup(Box(new Vector3(-16, -16, 64), new Vector3(16, 16, 96)));
    var rA = SpatialOverlap.Contact(apple, table, 128f);
    Check("contact A: box resting on a box top -> distance 0, penetration 0, normal +Z", rA.Distance == 0f && rA.Penetration < 0.01f && rA.NormalUp > 0.999f && rA.ContactPairs > 0, Show(rA));

    // B. flush against the +X side: distance 0, the host's normal is horizontal (+X).
    var sconce = Soup(Box(new Vector3(100, -16, 20), new Vector3(132, 16, 52)));
    var rB = SpatialOverlap.Contact(sconce, table, 128f);
    Check("contact B: box against a box side -> distance 0, normal horizontal (+X)", rB.Distance == 0f && rB.Penetration < 0.01f && MathF.Abs(rB.NormalUp) < 0.01f && rB.Normal.X > 0f, Show(rB));

    // C. a 5 u gap: distance 5, no contact pair, and the normal still says what it floats above.
    var above = Soup(Box(new Vector3(-16, -16, 69), new Vector3(16, 16, 101)));
    var rC = SpatialOverlap.Contact(above, table, 128f);
    Check("contact C: box 5 u above the top -> distance 5, no pairs, normal +Z (floating above a floor-like face)", MathF.Abs(rC.Distance - 5f) < 0.01f && rC.ContactPairs == 0 && rC.Penetration == 0f && rC.NormalUp > 0.999f, Show(rC));

    // D. sunk 10 u into the slab: surfaces cross (distance 0), the bottom vertices are 10 u inside, normal +Z.
    var sunk = Soup(Box(new Vector3(-16, -16, 54), new Vector3(16, 16, 86)));
    var rD = SpatialOverlap.Contact(sunk, table, 128f);
    Check("contact D: box sunk 10 u into a slab -> distance 0, penetration 10 along the contact normal, normal +Z", rD.Distance == 0f && MathF.Abs(rD.Penetration - 10f) < 0.01f && rD.NormalUp > 0.999f && rD.PenetratingVertices == 4, Show(rD));

    // E. a scaled dependent through TransformSoup: a 16 u model box at scale 2 is 32 u, placed so its bottom is the top.
    var model = Soup(Box(new Vector3(-8, -8, -8), new Vector3(8, 8, 8)));
    var scaled = SpatialOverlap.TransformSoup(model, new Vec3(0, 0, 80), new Vec3(0, 0, 0), 2f);
    var sb = SpatialOverlap.SoupBounds(scaled);
    var rE = SpatialOverlap.Contact(scaled, table, 128f);
    Check("contact E: scaled dependent (x2) lands with its bottom on the top -> 32 u box, distance 0", MathF.Abs(sb.Min.Z - 64f) < 0.01f && MathF.Abs(sb.Max.X - 16f) < 0.01f && rE.Distance == 0f && rE.Penetration < 0.01f && rE.NormalUp > 0.999f, $"bounds {sb}; {Show(rE)}");

    // F. a host rotated about two axes, the dependent rotated with it: the two still touch, and the host's normal is
    //    the tilted top — up-cosine cos 40°, a horizontal component of sin 40°. Exercises the transform on both soups.
    const float Deg = MathF.PI / 180f;
    var hostModel = Soup(Box(new Vector3(-100, -100, 0), new Vector3(100, 100, 64)));
    var depModel = Soup(Box(new Vector3(-16, -16, 64), new Vector3(16, 16, 96)));
    var rot = new Vec3(40 * Deg, 0, 30 * Deg); var at = new Vec3(1000, 2000, 300);
    var hostW = SpatialOverlap.TransformSoup(hostModel, at, rot, 1f);
    var depW = SpatialOverlap.TransformSoup(depModel, at, rot, 1f);
    var rF = SpatialOverlap.Contact(depW, hostW, 128f);
    float nxy = MathF.Sqrt(rF.Normal.X * rF.Normal.X + rF.Normal.Y * rF.Normal.Y) / rF.Normal.Length();
    Check("contact F: host and dependent rotated about two axes (x 40, z 30) -> still touching; normal up-cosine cos 40, horizontal sin 40",
          rF.Distance < 0.01f && rF.Penetration < 0.05f && MathF.Abs(rF.NormalUp - MathF.Cos(40 * Deg)) < 0.01f && MathF.Abs(nxy - MathF.Sin(40 * Deg)) < 0.01f, Show(rF));

    // G. two triangles through each other like an X: no vertex or edge of either touches the other, yet they cross.
    var tri1 = new TriangleSoup(new[] { new Vector3(-50, 0, -50), new Vector3(50, 0, -50), new Vector3(0, 0, 50) }, new[] { 0, 1, 2 });
    var tri2 = new TriangleSoup(new[] { new Vector3(0, -50, -50), new Vector3(0, 50, -50), new Vector3(0, 0, 50) }, new[] { 0, 1, 2 });
    var rG = SpatialOverlap.Contact(tri1, tri2, 128f);
    Check("contact G: two triangles crossing like an X -> distance 0 (the segment-through-triangle path)", rG.Distance == 0f, Show(rG));

    // H. two coplanar triangles overlapping as a star — no vertex of either inside the other: the edge-edge path.
    var up = new TriangleSoup(new[] { new Vector3(0, 40, 0), new Vector3(-35, -20, 0), new Vector3(35, -20, 0) }, new[] { 0, 1, 2 });
    var down = new TriangleSoup(new[] { new Vector3(0, -40, 0), new Vector3(35, 20, 0), new Vector3(-35, 20, 0) }, new[] { 0, 1, 2 });
    var rH = SpatialOverlap.Contact(up, down, 128f);
    Check("contact H: coplanar star of David -> distance 0 (edges cross, no vertex inside)", rH.Distance == 0f, Show(rH));

    // I. fully buried: an 8 u box in the middle of the slab. The surfaces are 28 u apart, yet every vertex is inside —
    //    penetration is read from the vertices, never from the surface distance, and it is the nearest way out.
    var buried = Soup(Box(new Vector3(-4, -4, 28), new Vector3(4, 4, 36)));
    var rI = SpatialOverlap.Contact(buried, table, 128f);
    Check("contact I: box buried in the middle of the slab -> surfaces 28 u apart, no contact so no contact penetration; the ENCLOSED reading says 28 with 8 vertices inside, and the host is closed so it counts",
          MathF.Abs(rI.Distance - 28f) < 0.01f && rI.Penetration == 0f && rI.ContactPairs == 0 && MathF.Abs(rI.EnclosedDepth - 28f) < 0.01f && rI.EnclosedVertices == 8 && rI.HostClosed && MathF.Abs(rI.EffectivePenetration - 28f) < 0.01f, Show(rI));

    // J. under the slab, flush with its underside: distance 0, the host's normal points DOWN — a ceiling.
    var hung = Soup(Box(new Vector3(-16, -16, -32), new Vector3(16, 16, 0)));
    var rJ = SpatialOverlap.Contact(hung, table, 128f);
    Check("contact J: box flush under the slab -> distance 0, normal −Z (hung from a ceiling)", rJ.Distance == 0f && rJ.Penetration < 0.01f && rJ.NormalUp < -0.999f, Show(rJ));

    // K. nothing within reach: the distance reads as the cap, and there is no normal to read.
    var far = Soup(Box(new Vector3(-16, -16, 500), new Vector3(16, 16, 532)));
    var rK = SpatialOverlap.Contact(far, table, 128f);
    Check("contact K: box 436 u above -> distance = the 128 u cap, no pairs, no normal", rK.Distance == 128f && rK.ContactPairs == 0 && rK.PenetratingVertices == 0 && float.IsNaN(rK.NormalUp), Show(rK));

    // L. the inside test at a convex edge: a point just outside the slab's corner is behind one face and in front of
    //    the other -> outside; a point just inside reads inside by the nearer way out.
    var grid = TriangleGrid.Build(table);
    var outside = grid.InsideDepth(new Vector3(101, 101, 32), 128f);
    var insideCorner = grid.InsideDepth(new Vector3(99, 98, 32), 128f);
    var onSurface = grid.InsideDepth(new Vector3(0, 0, 64), 128f);
    Check("contact L: a point outside a convex corner reads OUTSIDE; one 1 u inside reads inside by 1; one on the surface reads 0",
          outside.Depth == 0f && MathF.Abs(insideCorner.Depth - 1f) < 0.01f && onSurface.Depth == 0f, $"outside {outside.Depth:0.##}, inside {insideCorner.Depth:0.##}, on {onSurface.Depth:0.##}");

    // O. under an OPEN floor — a single quad, the shape most kit-piece collision takes. The box sits 8 u below the
    //    quad without touching it: distance 8, no contact, no contact penetration. The enclosed reading says "8 u
    //    behind the face" — which is why it is not the rule's number for an open host, and the host reads open.
    var quad = new TriangleSoup(new[] { new Vector3(-200, -200, 64), new Vector3(200, -200, 64), new Vector3(200, 200, 64), new Vector3(-200, 200, 64) }, new[] { 0, 1, 2, 0, 2, 3 });
    var underQuad = Soup(Box(new Vector3(-16, -16, 24), new Vector3(16, 16, 56)));
    var rO = SpatialOverlap.Contact(underQuad, quad, 128f);
    Check("contact O: box 8 u under an open floor quad -> distance 8, no contact penetration; the enclosed reading (40 — its bottom vertices behind the face) is flagged by an OPEN host and not effective",
          MathF.Abs(rO.Distance - 8f) < 0.01f && rO.ContactPairs == 0 && rO.Penetration == 0f && MathF.Abs(rO.EnclosedDepth - 40f) < 0.01f && !rO.HostClosed && rO.EffectivePenetration == 0f, Show(rO));

    // P. under a CLOSED slab, outside it: distance 28 to the underside, nothing inside, nothing through.
    var underSlab = Soup(Box(new Vector3(-16, -16, -60), new Vector3(16, 16, -28)));
    var rP = SpatialOverlap.Contact(underSlab, table, 128f);
    Check("contact P: box 28 u under a closed slab -> distance 28, no penetration of either kind", MathF.Abs(rP.Distance - 28f) < 0.01f && rP.Penetration == 0f && rP.EnclosedDepth == 0f && rP.HostClosed, Show(rP));

    // Q. a bench sunk 3 u into the table top and overhanging its edge — its bottom crosses the table's SIDE face too.
    //    The normal is read from the faces within the band weighted by parallelism, so the bottom-on-top contact
    //    outvotes the crossed side: up-cosine over 0.99, and the penetration is 3 along it, not the 40 u the
    //    overhanging corner sits behind the side face's plane. (Sunk deeper than the band, no parallel face is in the
    //    band and the normal comes from crossings alone — that regime is Sunk by penetration whatever the normal says.)
    var bench = Soup(Box(new Vector3(60, -16, 61), new Vector3(140, 16, 93)));
    var rQ = SpatialOverlap.Contact(bench, table, 128f);
    Check("contact Q: bench sunk 3 u and overhanging the edge -> normal +Z (the parallel contact outvotes the crossed side), penetration 3, not the overhang's 40",
          rQ.Distance == 0f && MathF.Abs(rQ.Penetration - 3f) < 0.1f && rQ.NormalUp > 0.99f, Show(rQ));

    // R. what winding does and does not touch: a host wound INWARD gives the same distance (winding-free), but the
    //    contact normal follows the host's faces and flips with them, and the penetration is read along it — which is
    //    why the decoder now turns every convex primitive outward by construction, and why a host on the render
    //    fallback never hosts.
    var inv = new TriangleSoup(table.Vertices, table.Indices.Chunk(3).SelectMany(t => new[] { t[0], t[2], t[1] }).ToArray());
    var rDi = SpatialOverlap.Contact(sunk, inv, 128f);
    Check("contact R: the same sunk box against the slab wound inward -> distance unchanged; the normal flips (up −1) and the readings that follow it read the part ABOVE the surface as inside (22)",
          rDi.Distance == 0f && rDi.NormalUp < -0.999f && MathF.Abs(rDi.Penetration - 22f) < 0.01f && MathF.Abs(rDi.EnclosedDepth - 22f) < 0.01f, Show(rDi));

    // S. two objects side by side on a floor, 5 u apart: their bottoms are coplanar and within a band of each other,
    //    but nothing FACES — the closest pair between the bottoms lies in their shared plane, not along a normal. The raw
    //    distance is 5 (the sides), the facing distance is 5 too (the sides face each other), and the normal is the
    //    neighbour's side, never its downward bottom; at a 1 u gap the bottoms are within 4 u of each other's edges and
    //    still do not vote.
    var neighbour = Soup(Box(new Vector3(21, -16, 0), new Vector3(53, 16, 30)));
    var bucket = Soup(Box(new Vector3(-16, -16, 0), new Vector3(16, 16, 40)));
    var rS = SpatialOverlap.Contact(bucket, neighbour, 128f, contactBand: 8f);
    var neighbour1 = Soup(Box(new Vector3(17, -16, 0), new Vector3(49, 16, 30)));
    var rS1 = SpatialOverlap.Contact(bucket, neighbour1, 128f, contactBand: 4f);
    Check("contact S: a box 5 u beside another on the same floor -> facing distance 5 through the sides, normal horizontal (the coplanar bottoms never vote)",
          MathF.Abs(rS.FacingDistance - 5f) < 0.01f && MathF.Abs(rS.NormalUp) < 0.01f && rS.Normal.X < 0f && rS.Penetration == 0f, Show(rS));
    Check("contact S: ...and at a 1 u gap, inside the band, the same: horizontal, no penetration", MathF.Abs(rS1.FacingDistance - 1f) < 0.01f && MathF.Abs(rS1.NormalUp) < 0.01f && rS1.Penetration == 0f, Show(rS1));

    // T. under a one-sided board, close to it: a bottle standing beneath a shelf board whose collision is a top face
    //    only. Its top is 3 u behind the face without crossing it — that is "under", not "through": no contact pair, no
    //    penetration, and the enclosed reading (behind the face by up to the bottle's height) is not effective on an
    //    open host. The same box 3 u ABOVE the face is a contact.
    var underBoard = Soup(Box(new Vector3(-16, -16, 29), new Vector3(16, 16, 61)));
    var rT = SpatialOverlap.Contact(underBoard, quad, 128f, contactBand: 8f);
    var onBoard = Soup(Box(new Vector3(-16, -16, 67), new Vector3(16, 16, 99)));
    var rT2 = SpatialOverlap.Contact(onBoard, quad, 128f, contactBand: 8f);
    Check("contact T: box 3 u under a one-sided board -> no contact pair, no effective penetration; 3 u above it -> a contact at 3 u",
          rT.ContactPairs == 0 && rT.EffectivePenetration == 0f && float.IsPositiveInfinity(rT.FacingDistance) == false && rT.FacingDistance >= 128f
       && rT2.ContactPairs > 0 && MathF.Abs(rT2.FacingDistance - 3f) < 0.01f && rT2.NormalUp > 0.999f, $"{Show(rT)} | {Show(rT2)}");

    // U. closure is read after welding: the same closed slab built with every face's four corners duplicated (24
    //    vertices, the way a hard-edged export or a compressed-mesh chunk stores them) still reads closed.
    var dupV = new List<Vector3>(); var dupI = new List<int>();
    for (int t = 0; t < table.TriangleCount; t++)
        for (int k = 0; k < 3; k++) { dupI.Add(dupV.Count); dupV.Add(table.Vertices[table.Indices[t * 3 + k]]); }
    var welded = TriangleGrid.Build(new TriangleSoup(dupV.ToArray(), dupI.ToArray()));
    var unwelded = TriangleGrid.Build(new TriangleSoup(new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0) }, new[] { 0, 1, 2 }));
    Check("contact U: a closed slab with every vertex duplicated per triangle reads CLOSED after welding; a lone triangle reads open", welded.IsClosed && !unwelded.IsClosed, $"welded {welded.IsClosed}, lone {unwelded.IsClosed}");

    // V. a book resting on a bedroll, the bedroll as the dependent: the book's bottom touches the bedroll's top, and
    //    nothing crosses. The bedroll is not "through" the book by its own thickness — the book's top face, which the
    //    bedroll's facets sit behind, is not a contact — so the reading is a touch from below (normal −Z), no penetration.
    var bedroll = Soup(Box(new Vector3(-50, -20, 0), new Vector3(50, 20, 12)));
    var book = Soup(Box(new Vector3(-10, -7, 12), new Vector3(10, 7, 16)));
    var rV = SpatialOverlap.Contact(bedroll, book, 128f, contactBand: 8f);
    Check("contact V: a book resting on a bedroll, read with the bedroll as the dependent -> a touch from below, normal −Z, no penetration",
          rV.FacingDistance == 0f && rV.NormalUp < -0.99f && rV.EffectivePenetration == 0f && rV.Penetration == 0f, Show(rV));

    // M. the box lower bound the broad phase uses.
    var bx1 = new Aabb(new Vec3(0, 0, 0), new Vec3(10, 10, 10), false);
    var bx2 = new Aabb(new Vec3(15, 13, 0), new Vec3(20, 20, 10), false);
    var bx3 = new Aabb(new Vec3(10, 0, 0), new Vec3(20, 10, 10), false);
    Check("contact M: BoxDistance is the corner gap (sqrt 34) and 0 for boxes that touch", MathF.Abs(SpatialOverlap.BoxDistance(bx1, bx2) - MathF.Sqrt(34f)) < 0.001f && SpatialOverlap.BoxDistance(bx1, bx3) == 0f,
          $"{SpatialOverlap.BoxDistance(bx1, bx2):0.###} / {SpatialOverlap.BoxDistance(bx1, bx3)}");

    // N. the same host indexed once and asked twice gives the same answers (the many-dependents path).
    var rA2 = SpatialOverlap.Contact(apple, grid, 128f);
    var rC2 = SpatialOverlap.Contact(above, grid, 128f);
    Check("contact N: a pre-built host grid answers the same as the one-shot call", rA2.Distance == rA.Distance && rA2.NormalUp == rA.NormalUp && rC2.Distance == rC.Distance, $"{Show(rA2)} | {Show(rC2)}");

    // W. how wide a surface is along a direction (the bound the placement-contact rule's R4 fold reads, 2026-09-03):
    //    the slab is 64 along +Z, 200 along +X, and 200·√2 along the XY diagonal.
    float eZ = grid.ExtentAlong(Vector3.UnitZ), eX = grid.ExtentAlong(Vector3.UnitX), eD = grid.ExtentAlong(Vector3.Normalize(new Vector3(1, 1, 0)));
    Check("contact W: ExtentAlong — the slab is 64 along Z, 200 along X, 200 x sqrt 2 along the XY diagonal", MathF.Abs(eZ - 64f) < 0.01f && MathF.Abs(eX - 200f) < 0.01f && MathF.Abs(eD - 200f * MathF.Sqrt(2f)) < 0.01f, $"z {eZ:0.##} x {eX:0.##} diag {eD:0.##}");

    // X. the reading R4 refuses, built by construction: a 30 u box with ONE face wound inward (the inverted-sliver
    //    residue the hull audit left — 75 of 1,367 vanilla hulls) still reads closed, and a 60 u box standing 3 u off
    //    that face has its far vertices "behind" their nearest jar face — an enclosed depth of 63 u inside a thing
    //    30 u across along the contact normal. No interior point of a closed object is further from its surface than
    //    half its width, so the rule reads the extent and refuses; the same shape against an honest 300 u block reads
    //    a depth well inside the block's extent. (A box wound inward on EVERY face does not reproduce it: a vertex
    //    whose nearest point is a corner ties three faces, and the tie breaks "behind every face".)
    var jarSoup = Soup(Box(new Vector3(-15, -15, 0), new Vector3(15, 15, 30)));
    var jarIdx = (int[])jarSoup.Indices.Clone();
    for (int t = 0; t < jarIdx.Length; t += 3)
    {
        var n = Vector3.Cross(jarSoup.Vertices[jarIdx[t + 1]] - jarSoup.Vertices[jarIdx[t]], jarSoup.Vertices[jarIdx[t + 2]] - jarSoup.Vertices[jarIdx[t]]);
        if (n.X > 0f) (jarIdx[t + 1], jarIdx[t + 2]) = (jarIdx[t + 2], jarIdx[t + 1]);   // the +X face only
    }
    var jarInv = new TriangleSoup(jarSoup.Vertices, jarIdx);
    var caseBeside = Soup(Box(new Vector3(18, -10, 5), new Vector3(78, 10, 25)));
    var jarGrid = TriangleGrid.Build(jarInv);
    var rX = SpatialOverlap.Contact(caseBeside, jarGrid, 128f);
    float jarExtent = jarGrid.ExtentAlong(Vector3.Normalize(rX.Normal));
    var block = Soup(Box(new Vector3(-150, -150, -50), new Vector3(150, 150, 250)));
    var caseInside = Soup(Box(new Vector3(-30, -30, 0), new Vector3(30, 30, 200)));
    var blockGrid = TriangleGrid.Build(block);
    var rX2 = SpatialOverlap.Contact(caseInside, blockGrid, 128f);
    float blockExtent = blockGrid.ExtentAlong(Vector3.Normalize(rX2.Normal));
    Check("contact X: a box 3 u off the one inverted face of a 30 u jar -> closed host, enclosed depth 63 u, extent along the normal 30 (the reading R4 refuses); a case inside a 300 u block -> depth 50, extent 300 (honest Sunk)",
          rX.HostClosed && MathF.Abs(rX.EffectivePenetration - 63f) < 0.5f && MathF.Abs(jarExtent - 30f) < 0.01f && rX.EffectivePenetration > jarExtent
       && rX2.HostClosed && MathF.Abs(rX2.EffectivePenetration - 50f) < 0.5f && MathF.Abs(blockExtent - 300f) < 0.01f && rX2.EffectivePenetration < blockExtent,
          $"jar: {Show(rX)} extent {jarExtent:0.##} | block: {Show(rX2)} extent {blockExtent:0.##}");
}

// Q46: positive bounding-box volume is not a surface-contact or compatibility verdict.
{
    static TriangleSoup Soup((List<Vector3> V, List<int> I) m) => new(m.V.ToArray(), m.I.ToArray());
    var outer = Box(new Vector3(-60), new Vector3(60));
    var inner = Box(new Vector3(-50), new Vector3(50));
    for (int i = 0; i < inner.I.Count; i += 3)
        (inner.I[i + 1], inner.I[i + 2]) = (inner.I[i + 2], inner.I[i + 1]);
    var shell = Soup(Merge(outer, inner));
    var objectInRoom = Soup(Box(new Vector3(-5), new Vector3(5)));
    var shellBounds = SpatialOverlap.MeasureAabbOverlap(SpatialOverlap.SoupBounds(shell), SpatialOverlap.SoupBounds(objectInRoom));
    var shellContact = SpatialOverlap.Contact(objectInRoom, shell, 128f);
    Check("Q46 hollow shell: bounds overlap 100%, surfaces remain 45 units apart",
        shellBounds.IntersectionVolume == 1000d && shellBounds.FractionOfSmaller == 1d
        && MathF.Abs(shellContact.Distance - 45f) < 0.001f && !shellContact.InContact
        && !shellContact.Crosses && shellContact.EffectivePenetration == 0f,
        $"volume={shellBounds.IntersectionVolume}, distance={shellContact.Distance}, penetration={shellContact.EffectivePenetration}");

    var bar = Soup(Box(new Vector3(-100, -1, -1), new Vector3(100, 1, 1)));
    var diagonal = new Vec3(0, 0, MathF.PI / 4);
    var first = SpatialOverlap.TransformSoup(bar, new Vec3(0, 0, 0), diagonal, 1f);
    var second = SpatialOverlap.TransformSoup(bar, new Vec3(10f / MathF.Sqrt(2), 10f / MathF.Sqrt(2), 0), diagonal, 1f);
    var barBounds = SpatialOverlap.MeasureAabbOverlap(SpatialOverlap.SoupBounds(first), SpatialOverlap.SoupBounds(second));
    var barContact = SpatialOverlap.Contact(first, second, 128f);
    Check("Q46 rotated thin bars: positive AABB overlap, surfaces remain 8 units apart",
        barBounds.IntersectionVolume > 0d && MathF.Abs(barContact.Distance - 8f) < 0.001f
        && !barContact.InContact && !barContact.Crosses && barContact.EffectivePenetration == 0f,
        $"volume={barBounds.IntersectionVolume}, distance={barContact.Distance}, penetration={barContact.EffectivePenetration}");

    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    bool cancelledLoudly = false;
    try { _ = SpatialOverlap.Contact(first, second, 128f, cancellationToken: cancelled.Token); }
    catch (OperationCanceledException) { cancelledLoudly = true; }
    Check("Q46 contact budget: a pre-cancelled query throws OperationCanceledException",
          cancelledLoudly, "cancellation must never be returned as a no-contact measurement");
}

Console.WriteLine($"\n==== {pass} passed, {fail} failed ====");
return fail == 0 ? 0 : 1;
