using System.Numerics;
using HousecarlCore;

// Milestone 4c — SYNTHETIC PROOF for the surface-crossing narrow phase.
//
// NO GAME DATA. Every case is a hand-built mesh whose right answer is known by construction,
// so this can run on a fresh checkout — the shape CONTRIBUTING.md asks proof to take.
//
// It exists because the corpus sweeps cannot tell "the primitive is right and Skyrim is
// noisy" from "the primitive is wrong". A wall must fire, a floor must not, and if those two
// are not nailed down first every percentage that follows is unreadable.

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

Console.WriteLine($"\n==== {pass} passed, {fail} failed ====");
return fail == 0 ? 0 : 1;
