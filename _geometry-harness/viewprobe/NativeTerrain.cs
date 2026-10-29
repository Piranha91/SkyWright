using System;
using System.Collections.Generic;
using System.Linq;
using SkyRender.World;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using V3 = System.Numerics.Vector3;

namespace ViewProbe;

/// <summary>
/// Turns the terrain sheets the walk produces (base patches pooled by texture, painted layers as
/// per-(cell, quadrant, layer) triangle lists with per-vertex opacity) back into the per-cell
/// <see cref="TerrainCell"/> data SkyRender draws natively.
///
/// It works from the sheets rather than from the LAND records so the fresh walk and the retained
/// walk (whose facts store exactly these sheets) feed it identically. Every value is recovered
/// exactly: a sheet vertex sits on the 128-unit grid, so its position names its cell and grid
/// vertex, and its Z and opacity are the record's own numbers.
/// </summary>
/// <summary>What the sheets do not carry: each cell's LAND vertex normals (VNML) and colours (VCLR),
/// and the normal map that goes with each landscape diffuse (its TXST's NormalOrGloss).</summary>
internal sealed class NativeTerrainExtras
{
    internal Dictionary<(int X, int Y), V3[]> Normals { get; } = new();
    internal Dictionary<(int X, int Y), V3[]> Colors { get; } = new();
    internal Dictionary<string, string> NormalForDiffuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes (or, when the record has none, drops) a LAND's normals and colours for its square.</summary>
    internal void TakeLand(ILandscapeGetter land, (int X, int Y) grid)
    {
        if (land.VertexNormals is { } vn) Normals[grid] = TerrainCell.DecodeNormals(Bytes(vn, p => (sbyte)p.X, p => (sbyte)p.Y, p => (sbyte)p.Z));
        else Normals.Remove(grid);
        if (land.VertexColors is { } vc) Colors[grid] = TerrainCell.DecodeColors(Bytes(vc, p => p.X, p => p.Y, p => p.Z));
        else Colors.Remove(grid);
    }

    // A 33x33 VNML / VCLR grid ([x, y], as VertexHeightMap.HeightMap is read) as x-fastest triples.
    static T[] Bytes<T>(IReadOnlyArray2d<P3UInt8> grid, Func<P3UInt8, T> x, Func<P3UInt8, T> y, Func<P3UInt8, T> z)
    {
        const int side = TerrainCell.Side;
        var b = new T[side * side * 3];
        for (int gy = 0; gy < side; gy++)
            for (int gx = 0; gx < side; gx++)
            {
                var p = grid[gx, gy];
                int i = (gy * side + gx) * 3;
                b[i] = x(p); b[i + 1] = y(p); b[i + 2] = z(p);
            }
        return b;
    }
}

internal static class NativeTerrain
{
    /// <summary>The cells the sheets cover, in a stable order (south to north, west to east).</summary>
    internal static List<TerrainCell> FromSheets(
        IReadOnlyDictionary<string, List<(V3 A, V3 B, V3 C)>> patches,
        IReadOnlyList<(string Diffuse, List<(V3 A, V3 B, V3 C, float Oa, float Ob, float Oc)> Tris)> layers,
        NativeTerrainExtras? extras = null)
    {
        TerrainTexture Tex(string diffuse)
            => new(diffuse, extras is not null && extras.NormalForDiffuse.TryGetValue(diffuse, out var n) ? n : null);

        var heights = new Dictionary<(int, int), float[]>();
        var baseTex = new Dictionary<(int, int), string?[]>();

        // Base patches: heights for every grid vertex, and each quadrant's base texture.
        foreach (var (diffuse, tris) in patches)
            foreach (var (a, b, c) in tris)
            {
                var (cell, quadrant) = Locate(a, b, c);
                var h = heights.TryGetValue(cell, out var hh) ? hh : heights[cell] = new float[TerrainCell.Side * TerrainCell.Side];
                foreach (var p in new[] { a, b, c })
                {
                    var (x, y) = GridVertex(cell, p);
                    h[y * TerrainCell.Side + x] = p.Z;
                }
                var bt = baseTex.TryGetValue(cell, out var bb) ? bb : baseTex[cell] = new string?[4];
                bt[quadrant] = diffuse.Length > 0 ? diffuse : null;
            }

        // Painted layers, in the walk's order (per cell, by layer number), which is the blend order.
        var layerLists = new Dictionary<(int, int), List<TerrainLayer>[]>();
        foreach (var (diffuse, tris) in layers)
        {
            if (tris.Count == 0) continue;
            var (cell, quadrant) = Locate(tris[0].A, tris[0].B, tris[0].C);
            int qx0 = (quadrant & 1) * 16, qy0 = quadrant >= 2 ? 16 : 0;
            var opacity = new float[TerrainCell.QuadrantSide * TerrainCell.QuadrantSide];
            foreach (var (a, b, c, oa, ob, oc) in tris)
            {
                Set(a, oa); Set(b, ob); Set(c, oc);
            }
            void Set(V3 p, float o)
            {
                var (x, y) = GridVertex(cell, p);
                opacity[(y - qy0) * TerrainCell.QuadrantSide + (x - qx0)] = o;
            }
            var lists = layerLists.TryGetValue(cell, out var ll) ? ll : layerLists[cell] = new[] { new List<TerrainLayer>(), new(), new(), new() };
            lists[quadrant].Add(new TerrainLayer(Tex(diffuse), opacity));
        }

        var cells = new List<TerrainCell>();
        foreach (var key in heights.Keys.OrderBy(k => k.Item2).ThenBy(k => k.Item1))
        {
            var cell = new TerrainCell(key.Item1, key.Item2, heights[key])
            {
                Normals = extras is not null && extras.Normals.TryGetValue(key, out var n) ? n : null,
                Colors = extras is not null && extras.Colors.TryGetValue(key, out var c) ? c : null,
            };
            var bt = baseTex[key];
            layerLists.TryGetValue(key, out var lists);
            for (int q = 0; q < 4; q++)
                cell.Quadrants[q] = new TerrainQuadrant
                {
                    Base = bt[q] is { } d ? Tex(d) : null,
                    Layers = lists?[q] ?? (IReadOnlyList<TerrainLayer>)Array.Empty<TerrainLayer>(),
                };
            cells.Add(cell);
        }
        return cells;
    }

    /// <summary>The cell and quadrant a grid triangle lies in, from its centroid.</summary>
    static ((int, int) Cell, int Quadrant) Locate(V3 a, V3 b, V3 c)
    {
        float cx = (a.X + b.X + c.X) / 3f, cy = (a.Y + b.Y + c.Y) / 3f;
        int gx = (int)MathF.Floor(cx / TerrainCell.CellSize), gy = (int)MathF.Floor(cy / TerrainCell.CellSize);
        int sx = (int)MathF.Floor((cx - gx * TerrainCell.CellSize) / TerrainCell.VertexSpacing);
        int sy = (int)MathF.Floor((cy - gy * TerrainCell.CellSize) / TerrainCell.VertexSpacing);
        return ((gx, gy), (sy < 16 ? 0 : 2) + (sx < 16 ? 0 : 1));
    }

    /// <summary>The grid vertex of a sheet vertex within its triangle's cell (0..32 each way).</summary>
    static (int X, int Y) GridVertex((int X, int Y) cell, V3 p)
        => ((int)MathF.Round((p.X - cell.X * TerrainCell.CellSize) / TerrainCell.VertexSpacing),
            (int)MathF.Round((p.Y - cell.Y * TerrainCell.CellSize) / TerrainCell.VertexSpacing));
}
