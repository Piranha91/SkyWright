using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Skyrim;

// `navownprobe --nav-invariants OUT.json PLUGIN...`: what a navmesh repair must keep true, measured on each plugin's own NAVM
// records through Mutagen's model and the one grid decode (SkyWright.Engine.NavmeshGridDecode). Gate B of the navmesh repair
// task re-measures here the five figures its Gate A survey took with a scratch decoder:
//   1. the grid is 2-D (row = y, column = x): each cell entry's triangle overlaps its cell in plan, against the other reading;
//      no triangle in no cell; no entry past the triangle count;
//   2. the box (Min/Max) is the extent of the vertices triangles use, in x, y and z, within 1 u;
//   3. the section lengths are the box's x and y extent over the divisor;
//   4. CrcHash is 0xA5E9A03C;
//   5. the grid bytes decode to exactly divisor² cells.
static class NavInvariants
{
    public static int Run(string outPath, IReadOnlyList<string> plugins)
    {
        var report = new List<object>();
        foreach (var path in plugins)
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            long records = 0, noData = 0, noTris = 0, decoded = 0, decodeFailed = 0, crcTag = 0, sections = 0, entries = 0, rowY = 0, rowX = 0,
                 past = 0, unlisted = 0, triangles = 0;
            var box = new long[3]; var boxAll = new long[3];
            var failures = new List<string>();
            foreach (var nm in mod.EnumerateMajorRecords<INavigationMeshGetter>())
            {
                records++;
                if (nm.IsDeleted || nm.Data is not { } d) { noData++; continue; }
                if (d.CrcHash == 0xA5E9A03C) crcTag++;
                var tris = d.Triangles; var vs = d.Vertices;
                if (tris.Count == 0) { noTris++; continue; }
                triangles += tris.Count;
                var used = new HashSet<int>();
                foreach (var t in tris) { used.Add(t.Vertices.X); used.Add(t.Vertices.Y); used.Add(t.Vertices.Z); }
                used.RemoveWhere(i => i < 0 || i >= vs.Count);
                float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
                foreach (var i in used) { var v = vs[i]; float[] c = { v.X, v.Y, v.Z }; for (int a = 0; a < 3; a++) { lo[a] = Math.Min(lo[a], c[a]); hi[a] = Math.Max(hi[a], c[a]); } }
                float[] mn = { d.Min.X, d.Min.Y, d.Min.Z }, mx = { d.Max.X, d.Max.Y, d.Max.Z };
                for (int a = 0; a < 3; a++) if (Math.Abs(mn[a] - lo[a]) <= 1 && Math.Abs(mx[a] - hi[a]) <= 1) box[a]++;
                float[] loAll = { float.MaxValue, float.MaxValue, float.MaxValue }, hiAll = { float.MinValue, float.MinValue, float.MinValue };
                foreach (var v in vs) { float[] c = { v.X, v.Y, v.Z }; for (int a = 0; a < 3; a++) { loAll[a] = Math.Min(loAll[a], c[a]); hiAll[a] = Math.Max(hiAll[a], c[a]); } }
                for (int a = 0; a < 3; a++) if (Math.Abs(mn[a] - loAll[a]) <= 1 && Math.Abs(mx[a] - hiAll[a]) <= 1) boxAll[a]++;
                uint div = d.NavmeshGridDivisor;
                if (div > 0 && Math.Abs(d.MaxDistanceX - (d.Max.X - d.Min.X) / div) <= 1 && Math.Abs(d.MaxDistanceY - (d.Max.Y - d.Min.Y) / div) <= 1) sections++;
                var cells = NavmeshGridDecode.Cells(div, d.NavmeshGrid.Span, out var why);
                if (cells is null) { decodeFailed++; if (failures.Count < 10) failures.Add($"{nm.FormKey}: {why}"); continue; }
                decoded++;
                var listed = new HashSet<int>();
                for (int k = 0; k < cells.Length; k++)
                {
                    int r = k / (int)div, c = k % (int)div;
                    foreach (var ti in cells[k])
                    {
                        entries++;
                        if (ti >= tris.Count) { past++; continue; }
                        listed.Add(ti);
                        var t = tris[ti];
                        var p = new[] { t.Vertices.X, t.Vertices.Y, t.Vertices.Z }.Where(i => i >= 0 && i < vs.Count).Select(i => vs[i]).ToList();
                        if (p.Count == 0) continue;
                        bool Over(int ix, int iy) =>
                            p.Min(q => q.X) <= d.Min.X + (ix + 1) * d.MaxDistanceX + 1 && p.Max(q => q.X) >= d.Min.X + ix * d.MaxDistanceX - 1 &&
                            p.Min(q => q.Y) <= d.Min.Y + (iy + 1) * d.MaxDistanceY + 1 && p.Max(q => q.Y) >= d.Min.Y + iy * d.MaxDistanceY - 1;
                        if (Over(c, r)) rowY++;
                        if (Over(r, c)) rowX++;
                    }
                }
                unlisted += tris.Count - listed.Count;
            }
            report.Add(new
            {
                plugin = Path.GetFileName(path), records, deletedOrNoData = noData, noTriangles = noTris, triangles,
                crcIsTag = crcTag, gridDecoded = decoded, gridDecodeFailed = decodeFailed, decodeFailures = failures,
                sectionLengthsAreExtentOverDivisor = sections,
                gridEntries = entries, entriesOverlapRowYColX = rowY, entriesOverlapRowXColY = rowX, entriesPastCount = past, trianglesInNoCell = unlisted,
                boxIsUsedVertexExtent = new { x = box[0], y = box[1], z = box[2] },
                boxIsAllVertexExtent = new { x = boxAll[0], y = boxAll[1], z = boxAll[2] },
            });
        }
        File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(outPath));
        return 0;
    }
}
