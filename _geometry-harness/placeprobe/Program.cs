using System.Diagnostics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore.Progress;
using SkyWright.Engine;

// Milestone-1 proof harness: run PlacementExtract over a REAL plugin and report what came out.
// usage: placeprobe <plugin.esp|esm> [--refs <FormID:Plugin>] [--cell <EditorID>]

using var __run = RunLog.BeginProcess("placeprobe", args);

if (args.Length == 0) { Console.Error.WriteLine("usage: placeprobe <plugin> [--ref 0xFORMID] [--cell EDID]"); return 1; }
var path = args[0];
string? wantCell = null; uint wantRef = 0;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--cell") wantCell = args[i + 1];
    if (args[i] == "--ref") wantRef = Convert.ToUInt32(args[i + 1], 16);
}

var sw = Stopwatch.StartNew();
using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
Console.WriteLine($"opened {Path.GetFileName(path)}  ({new FileInfo(path).Length / 1024.0 / 1024:0.0} MB)  in {sw.ElapsedMilliseconds} ms");

sw.Restart();
var cells = PlacementExtract.FromMod(mod).ToList();
sw.Stop();

long refs = cells.Sum(c => (long)c.Refs.Count);
long navs = cells.Sum(c => (long)c.Navmeshes.Count);
long navVerts = cells.Sum(c => c.Navmeshes.Sum(n => (long)n.Vertices.Count));
long navTris = cells.Sum(c => c.Navmeshes.Sum(n => (long)n.Triangles.Count));

Console.WriteLine($"extract: {cells.Count:N0} cells, {refs:N0} placed refs, {navs:N0} navmeshes "
                + $"({navVerts:N0} verts / {navTris:N0} tris) in {sw.ElapsedMilliseconds:N0} ms");
Console.WriteLine($"  interior cells {cells.Count(c => c.Interior):N0} | exterior {cells.Count(c => !c.Interior):N0} "
                + $"| worldspaces {cells.Where(c => !c.Interior).Select(c => c.Worldspace).Distinct().Count()}");

Console.WriteLine("\n-- refs by record type --");
foreach (var g in cells.SelectMany(c => c.Refs).GroupBy(r => r.RecordType).OrderByDescending(g => g.Count()))
    Console.WriteLine($"   {g.Key,-16} {g.Count(),9:N0}");

var all = cells.SelectMany(c => c.Refs).ToList();
Console.WriteLine("\n-- health --");
Console.WriteLine($"   no Base link      {all.Count(r => r.Base is null),9:N0}");
Console.WriteLine("\n-- base link by arm (Base: the REFR/ACHR link; arm base: each arm's own link, PlacedRef.ArmBase) --");
foreach (var g in all.GroupBy(r => r.RecordType).OrderByDescending(g => g.Count()))
    Console.WriteLine($"   {g.Key,-16} {g.Count(),9:N0} refs  Base {g.Count(r => r.Base is not null),9:N0}  arm base {g.Count(r => r.ArmBase is not null),9:N0}"
                    + $"  via {string.Join("/", g.Select(r => r.ArmBaseLink ?? "-").Distinct())}");
Console.WriteLine($"   InitiallyDisabled {all.Count(r => r.InitiallyDisabled),9:N0}");
Console.WriteLine($"   IsDeleted         {all.Count(r => r.Deleted),9:N0}");
Console.WriteLine($"   Persistent        {all.Count(r => r.Persistent),9:N0}");
Console.WriteLine($"   Scale != 1.0      {all.Count(r => Math.Abs(r.Scale - 1f) > 1e-6),9:N0}");
Console.WriteLine($"   Rotation != 0     {all.Count(r => r.Rotation != new Vec3(0, 0, 0)),9:N0}");
Console.WriteLine($"   Position == 0,0,0 {all.Count(r => r.Position == new Vec3(0, 0, 0)),9:N0}");

// Rotation units. If RADIANS, the BULK sits within [0, 2pi]; degrees would put the bulk in the hundreds.
// A max alone is a bad test: the CK happily stores multi-turn angles (1800 deg = 10pi rad), so outliers exist.
var rotVals = all.SelectMany(r => new[] { Math.Abs(r.Rotation.X), Math.Abs(r.Rotation.Y), Math.Abs(r.Rotation.Z) })
                 .Where(v => v > 0).OrderBy(v => v).ToArray();
if (rotVals.Length > 0)
{
    float P(double q) => rotVals[Math.Min(rotVals.Length - 1, (int)(q * rotVals.Length))];
    int overTurn = rotVals.Count(v => v > 2 * Math.PI + 1e-3);
    Console.WriteLine($"   |rot| p50/p99/max {P(.50),6:0.###} {P(.99),6:0.###} {rotVals[^1],7:0.###}"
                    + $"   >2pi: {overTurn:N0} ({100.0 * overTurn / rotVals.Length:0.###}%)");
    Console.WriteLine($"   -> {(P(.99) <= 2 * Math.PI + 1e-3 ? "RADIANS confirmed (p99 within one turn; tail is multi-turn winding, normalize mod 2pi)" : "NOT radians - investigate")}");
}

// Exterior grid vs position: a cell is 4096 units across, and cell (gx,gy) covers
// [gx*4096, (gx+1)*4096). If our grid + position agree, most refs land inside their own cell.
var ext = cells.Where(c => !c.Interior && c.Grid is not null).ToList();
if (ext.Count > 0)
{
    long tIn = 0, tOut = 0, pIn = 0, pOut = 0;
    foreach (var c in ext)
    {
        var (gx, gy) = c.Grid!.Value;
        foreach (var r in c.Refs)
        {
            bool ok = r.Position.X >= gx * 4096f && r.Position.X < (gx + 1) * 4096f
                   && r.Position.Y >= gy * 4096f && r.Position.Y < (gy + 1) * 4096f;
            if (r.Persistent) { if (ok) pIn++; else pOut++; }
            else { if (ok) tIn++; else tOut++; }
        }
    }
    double tp = tIn + tOut == 0 ? 0 : 100.0 * tIn / (tIn + tOut);
    double pp = pIn + pOut == 0 ? 0 : 100.0 * pIn / (pIn + pOut);
    Console.WriteLine($"\n-- grid<->position cross-check ({ext.Count:N0} exterior cells) --");
    Console.WriteLine($"   TEMPORARY refs inside their own cell box : {tIn,9:N0} / {tIn + tOut,9:N0}  ({tp:0.00}%)");
    Console.WriteLine($"   PERSISTENT refs inside their own cell box: {pIn,9:N0} / {pIn + pOut,9:N0}  ({pp:0.00}%)");
    Console.WriteLine("   (persistent refs are parked in the worldspace persistent cell but positioned anywhere,");
    Console.WriteLine("    so only the TEMPORARY figure tests whether grid and position agree)");
}

// Navmesh coordinate space: are NAVM vertices worldspace-absolute, or cell-local?
// Test against the SAME 4096-unit cell box. This is the question the charter flagged as unverified.
var navCells = ext.Where(c => c.Navmeshes.Any(n => n.Vertices.Count > 0)).Take(200).ToList();
if (navCells.Count > 0)
{
    int absHit = 0, locHit = 0, neither = 0;
    foreach (var c in navCells)
    {
        var (gx, gy) = c.Grid!.Value;
        var v = c.Navmeshes.First(n => n.Vertices.Count > 0).Vertices[0];
        bool abs = v.X >= gx * 4096f - 512 && v.X < (gx + 1) * 4096f + 512
                && v.Y >= gy * 4096f - 512 && v.Y < (gy + 1) * 4096f + 512;
        bool loc = Math.Abs(v.X) <= 4608 && Math.Abs(v.Y) <= 4608;
        if (abs) absHit++; else if (loc) locHit++; else neither++;
    }
    Console.WriteLine($"\n-- NAVM coordinate space ({navCells.Count} sampled exterior cells) --");
    Console.WriteLine($"   vertex[0] inside cell's WORLD box : {absHit}");
    Console.WriteLine($"   plausibly CELL-LOCAL instead      : {locHit}");
    Console.WriteLine($"   neither                           : {neither}");
    Console.WriteLine($"   => {(absHit > navCells.Count * 0.8 ? "NAVM vertices are WORLDSPACE-ABSOLUTE" : "inconclusive - do NOT assume")}");
}

if (wantCell is not null)
{
    var c = cells.FirstOrDefault(x => string.Equals(x.CellEditorId, wantCell, StringComparison.OrdinalIgnoreCase));
    if (c is null) Console.WriteLine($"\ncell '{wantCell}' not found in this plugin");
    else
    {
        Console.WriteLine($"\n-- cell {c.CellEditorId} {c.Cell} interior={c.Interior} grid={c.Grid} --");
        Console.WriteLine($"   {c.Refs.Count} refs, {c.Navmeshes.Count} navmeshes");
        foreach (var r in c.Refs.Take(15))
            Console.WriteLine($"   {r.FormKey} {r.RecordType,-12} base={r.Base} pos={r.Position} rot={r.Rotation} scale={r.Scale:0.###}"
                            + $"{(r.InitiallyDisabled ? " [disabled]" : "")}{(r.Persistent ? " [persist]" : "")}");
    }
}

if (wantRef != 0)
{
    var hit = cells.SelectMany(c => c.Refs.Select(r => (c, r))).FirstOrDefault(t => t.r.FormKey.ID == wantRef);
    if (hit.r is null) Console.WriteLine($"\nref {wantRef:X6} not found");
    else Console.WriteLine($"\n-- ref {hit.r.FormKey} --\n   type={hit.r.RecordType} edid={hit.r.EditorId} base={hit.r.Base}\n"
                         + $"   pos={hit.r.Position} rot={hit.r.Rotation} scale={hit.r.Scale}\n"
                         + $"   cell={hit.c.CellEditorId} {hit.c.Cell} grid={hit.c.Grid} interior={hit.c.Interior}");
}

return 0;
