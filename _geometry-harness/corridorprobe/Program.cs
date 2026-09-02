using System.Numerics;
using System.Text;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// corridorprobe — choose and measure navmesh CORRIDORS from the records, for roadmap 4.7 Part C.
//
// The in-game matrix needs navmesh strips of known widths. This reads a cell's winning navmesh straight out of the
// plugin and (a) draws it as a coverage map so a corridor can be picked by eye, (b) measures the walkable width across
// a chosen point and heading, plus the clearance ladder the route rule would report for the corridor on its own.
//
// usage: corridorprobe map <CellEditorID> [--step 32] [--plugin Skyrim.esm]
//        corridorprobe width <CellEditorID> --at X,Y,Z --heading DEG [--span 600] [--plugin Skyrim.esm]
//   map    an ASCII coverage map of the cell's navmesh at --step units per character (rows = Y descending)
//   width  the walkable width across the corridor at the point, perpendicular to the heading (yaw, degrees, 0 = +Y,
//          clockwise), sampled every 4 u; and the width every 64 u along the heading for --span each way

string GamePath = HarnessTarget.Require(HarnessTarget.Game, "game directory", "game");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length < 2) { Console.Error.WriteLine("usage: corridorprobe map|width <CellEditorID> [...]  |  corridorprobe find <mesh substring>"); return 1; }
string mode = args[0], cellId = args[1];

if (mode == "find")
{
    // Base records whose model path contains the substring — the FormIDs the in-game arms place with PlaceAtMe.
    using var fm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dataDir, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
    foreach (var rec in fm.EnumerateMajorRecords())
        if (rec is IModeledGetter mg && mg.Model?.File is { } f && f.GivenPath.Contains(cellId, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"   {rec.FormKey}  {rec.GetType().Name.Replace("BinaryOverlay", ""),-16} {rec.EditorID,-32} {f.GivenPath}");
    return 0;
}
float step = 32f, span = 600f, heading = 0f; Vec3? at = null; string plugin = "Skyrim.esm";
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--step" && i + 1 < args.Length) step = float.Parse(args[++i]);
    else if (args[i] == "--span" && i + 1 < args.Length) span = float.Parse(args[++i]);
    else if (args[i] == "--heading" && i + 1 < args.Length) heading = float.Parse(args[++i]);
    else if (args[i] == "--plugin" && i + 1 < args.Length) plugin = args[++i];
    else if (args[i] == "--at" && i + 1 < args.Length) { var p = args[++i].Split(','); at = new Vec3(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2])); }
}

using var mod = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dataDir, plugin), SkyrimRelease.SkyrimSE);
CellPlacements? cell = null;
foreach (var cp in PlacementExtract.FromMod(mod))
    if (string.Equals(cp.CellEditorId, cellId, StringComparison.OrdinalIgnoreCase)) { cell = cp; break; }
if (cell is null) { Console.Error.WriteLine($"cell '{cellId}' not found in {plugin}"); return 1; }
var navs = NavmeshIndex.Build(cell.Navmeshes, out int malformed);
Console.WriteLine($"{cellId}: {cell.Navmeshes.Count} navmesh(es), {navs.TriangleCount:N0} triangles ({malformed} malformed), {cell.Refs.Count:N0} refs, {(cell.Interior ? "interior" : "exterior")}");

var box = Aabb.Empty;
foreach (var nv in cell.Navmeshes) foreach (var v in nv.Vertices) box = box.Add(new Vector3(v.X, v.Y, v.Z));
Console.WriteLine($"navmesh extent: {box}");

// Ground under (x,y): every non-deleted triangle covering the point; returns the heights found.
List<float> GroundAt(float x, float y)
{
    var zs = new List<float>();
    var probe = new Aabb(new Vec3(x - 0.5f, y - 0.5f, box.Min.Z - 512), new Vec3(x + 0.5f, y + 0.5f, box.Max.Z + 512), false);
    foreach (var hit in navs.Query(probe))
    {
        if ((hit.Flags & 0x08) != 0) continue;
        var nav = navs.NavAt(hit.NavIndex); var tri = nav.Triangles[hit.TriangleIndex];
        if (RouteConnectivity.HeightAt(nav.Vertices[tri.A], nav.Vertices[tri.B], nav.Vertices[tri.C], x, y) is { } z) zs.Add(z);
    }
    return zs;
}

if (mode == "map")
{
    int nx = (int)MathF.Ceiling((box.Max.X - box.Min.X) / step) + 1, ny = (int)MathF.Ceiling((box.Max.Y - box.Min.Y) / step) + 1;
    Console.WriteLine($"map: {nx} x {ny} cells of {step} u; '#' = one storey, 'N' = several storeys, '.' = none; columns every 8 = {8 * step} u");
    Console.WriteLine($"     x from {box.Min.X:0} (left) to {box.Max.X:0}; y from {box.Max.Y:0} (top) to {box.Min.Y:0}");
    var header = new StringBuilder("        ");
    for (int i = 0; i < nx; i++) header.Append(i % 8 == 0 ? '|' : ' ');
    Console.WriteLine(header);
    for (int j = ny - 1; j >= 0; j--)
    {
        float y = box.Min.Y + j * step;
        var sb = new StringBuilder($"{y,7:0} ");
        for (int i = 0; i < nx; i++)
        {
            float x = box.Min.X + i * step;
            var zs = GroundAt(x, y);
            sb.Append(zs.Count == 0 ? '.' : zs.Max() - zs.Min() > 128f ? 'N' : '#');
        }
        Console.WriteLine(sb);
    }
    return 0;
}

if (mode == "width")
{
    if (at is null) { Console.Error.WriteLine("width needs --at X,Y,Z (Z may be -99999 for the lowest storey under X,Y)"); return 1; }
    if (at.Value.Z <= -99999f)
    {
        var zs = GroundAt(at.Value.X, at.Value.Y);
        if (zs.Count == 0) { Console.Error.WriteLine("no navmesh under that point"); return 1; }
        at = new Vec3(at.Value.X, at.Value.Y, zs.Min());
        Console.WriteLine($"storeys under the point: {string.Join(", ", zs.OrderBy(z => z).Select(z => z.ToString("0")))} -> using {at.Value.Z:0}");
    }
    float rad = heading * MathF.PI / 180f;
    var dir = new Vector2(MathF.Sin(rad), MathF.Cos(rad));          // yaw 0 = +Y, clockwise
    var perp = new Vector2(dir.Y, -dir.X);                           // to the right of travel
    float z0 = at.Value.Z;

    (float left, float right, float w) WidthAt(float cx, float cy)
    {
        float Ground(float x, float y)
        {
            var zs = GroundAt(x, y).Where(z => MathF.Abs(z - z0) <= 160f).ToList();
            return zs.Count == 0 ? float.NaN : zs.OrderBy(z => MathF.Abs(z - z0)).First();
        }
        if (float.IsNaN(Ground(cx, cy))) return (0, 0, 0);
        float l = 0, r = 0;
        for (float s = 4; s <= 2000; s += 4) { if (float.IsNaN(Ground(cx - perp.X * s, cy - perp.Y * s))) break; l = s; }
        for (float s = 4; s <= 2000; s += 4) { if (float.IsNaN(Ground(cx + perp.X * s, cy + perp.Y * s))) break; r = s; }
        return (l, r, l + r);
    }

    var w0 = WidthAt(at.Value.X, at.Value.Y);
    Console.WriteLine($"at ({at.Value.X:0}, {at.Value.Y:0}, {z0:0}) heading {heading:0}: walkable width across = {w0.w:0} u  (left {w0.left:0}, right {w0.right:0}; 4 u resolution)");
    Console.WriteLine($"   along the heading, every 64 u for {span:0} u each way:");
    for (float s = -span; s <= span; s += 64)
    {
        float cx = at.Value.X + dir.X * s, cy = at.Value.Y + dir.Y * s;
        var w = WidthAt(cx, cy);
        var gz = GroundAt(cx, cy).Where(z => MathF.Abs(z - z0) <= 160f).OrderBy(z => MathF.Abs(z - z0)).ToList();
        Console.WriteLine($"   {s,6:+0;-0} u  ({cx,7:0}, {cy,7:0})  z {(gz.Count > 0 ? gz[0].ToString("0") : "  -"),5}  width {w.w,5:0}  left {w.left,4:0} right {w.right,4:0}");
    }

    // The corridor's own clearance ladder as the route rule sees it: a phantom 1 u obstacle at the point.
    var phantom = MeshColumnIndex.Build(Array.Empty<Vector3>(), Array.Empty<int>());
    var pbox = new Aabb(new Vec3(at.Value.X - 0.5f, at.Value.Y - 0.5f, z0), new Vec3(at.Value.X + 0.5f, at.Value.Y + 0.5f, z0 + 100), false);
    var ladder = new[] { 0f, 32f, 64f, 96f, 128f, 160f, 192f, 256f };
    var facts = RouteConnectivity.Measure(navs, phantom, pbox, 256f, 16f, 32f, 128f, ladder);
    Console.WriteLine($"   route rule, corridor alone (window ±256 u, step 16): judged={facts.Judged} storeys={facts.Storeys} intact-without rung {facts.MaxIntactRungWithout} = {(facts.MaxIntactRungWithout < 0 ? 0 : ladder[facts.MaxIntactRungWithout]):0} u  {facts.Why}");
    return 0;
}

Console.Error.WriteLine("mode must be map or width");
return 1;
