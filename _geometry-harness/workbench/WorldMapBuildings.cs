using System.Numerics;
using HousecarlCore;

namespace Workbench;

/// <summary>The render triangles of the meshes a load order's buildings use, for the World tab's map. A mesh is
/// found through the load order's own asset view (loose files and archives, by the shared resolver) and read by the
/// shared reader (<see cref="NifGeometry.MeasureVisibleRender"/>), once; one that is missing or cannot be read is
/// remembered as such and its building is drawn as its box.</summary>
internal sealed class BuildingMeshes : IDisposable
{
    readonly AssetResolver _assets;
    readonly Dictionary<string, TriangleSoup?> _soups = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    BuildingMeshes(AssetResolver assets) => _assets = assets;

    /// <summary>The asset view of a load order: the profile's enabled mods and active archives, or the candidate
    /// order file's, as the probes build it.</summary>
    internal static BuildingMeshes For(LoadOrderSpec o)
    {
        string profileDir = Path.Combine(o.Instance, "profiles", o.Profile), mods = Path.Combine(o.Instance, "mods"),
               overwrite = Path.Combine(o.Instance, "overwrite"), data = Path.Combine(o.Game, "Data");
        if (o.OrderFile is null)
        {
            var comp = Mo2LoadOrder.ReadComposition(profileDir);
            var arch = ArchiveDiscovery.Discover(profileDir, mods, data, overwrite, o.Game);
            return new BuildingMeshes(AssetResolver.Build(overwrite, mods, data, comp.EnabledMods, arch.Archives));
        }
        var cfg = HarnessShared.HarnessOrderCli.Resolve(o.Profile, profileDir, mods, data, overwrite, o.Game, o.OrderFile, null, null);
        if (cfg.Refused) throw new InvalidOperationException(cfg.RefusedReason ?? $"the order file '{o.OrderFile}' was refused.");
        return new BuildingMeshes(AssetResolver.Build(overwrite, mods, data, cfg.EnabledMods, cfg.Archives.Archives));
    }

    /// <summary>A mesh's visible render triangles in model space, or null when it is missing, unreadable or incomplete.</summary>
    internal TriangleSoup? SoupOf(string meshPath)
    {
        lock (_gate)
        {
            if (_soups.TryGetValue(meshPath, out var known)) return known;
            TriangleSoup? soup = null;
            try
            {
                var full = meshPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? meshPath : Path.Combine("meshes", meshPath);
                var res = _assets.ResolveForPlacement(full);
                var bytes = res.Sources.Count == 0 ? null : AssetResolver.ReadPlacementSource(res.Sources[0]).Bytes;
                if (bytes is not null && NifGeometry.MeasureVisibleRender(bytes, includeTriangles: true).Model is { Soup: { TriangleCount: > 0 } s } m
                    && m.RenderComplete && m.Shapes.Where(x => !x.Hidden).All(x => x.PoseIssue is null))
                    soup = s;
            }
            catch (Exception) { soup = null; }               // a mesh the reader cannot take is drawn as its box
            return _soups[meshPath] = soup;
        }
    }

    public void Dispose() { lock (_gate) _assets.Dispose(); }
}

/// <summary>Buildings seen from straight above, drawn into a picture: each mesh's triangles at the reference's place,
/// the highest surface kept at every pixel, shaded by how the surface faces a light from the north-west, with a dark
/// line where a building ends or its height steps (an eave, a wall top). Shapes only: no textures.</summary>
internal static class BuildingRaster
{
    /// <summary>World units of height step between neighbouring pixels that draws a line, over what a steep roof gives.</summary>
    internal const float StepLine = 48f;

    /// <summary>Paint <paramref name="solids"/> (world-space triangles) and <paramref name="boxes"/> (closed X,Y rings,
    /// drawn flat for a building whose mesh could not be read) into a premultiplied BGRA picture of
    /// <paramref name="w"/> by <paramref name="h"/> pixels. Pixel (0,0) is the north-west corner at world
    /// (<paramref name="west"/>, <paramref name="north"/>); a pixel is <paramref name="unitsPerPixel"/> world units.
    /// Uncovered pixels are transparent.</summary>
    internal static byte[] Paint(int w, int h, double west, double north, double unitsPerPixel,
                                 IEnumerable<TriangleSoup> solids, IEnumerable<float[]> boxes, CancellationToken ct = default)
    {
        var z = new float[w * h];
        Array.Fill(z, float.NegativeInfinity);
        var lit = new float[w * h];
        var light = Vector3.Normalize(new Vector3(-0.5f, 0.5f, 0.75f));   // towards the light: west, north and up
        double inv = 1.0 / unitsPerPixel;

        void Triangle(Vector3 a, Vector3 b, Vector3 c, float shade)
        {
            double ax = (a.X - west) * inv, ay = (north - a.Y) * inv, bx = (b.X - west) * inv, by = (north - b.Y) * inv, cx = (c.X - west) * inv, cy = (north - c.Y) * inv;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            if (x0 > x1 || y0 > y1) return;
            double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(area) < 1e-9) return;               // seen edge-on from above: a wall, or a sliver
            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    double sx = px + 0.5, sy = py + 0.5;
                    double w0 = ((bx - sx) * (cy - sy) - (by - sy) * (cx - sx)) / area;
                    double w1 = ((cx - sx) * (ay - sy) - (cy - sy) * (ax - sx)) / area;
                    double w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float height = (float)(w0 * a.Z + w1 * b.Z + w2 * c.Z);
                    int at = py * w + px;
                    if (height <= z[at]) continue;
                    z[at] = height; lit[at] = shade;
                }
        }

        foreach (var soup in solids)
        {
            ct.ThrowIfCancellationRequested();
            var v = soup.Vertices; var ix = soup.Indices;
            for (int i = 0; i + 2 < ix.Length; i += 3)
            {
                Vector3 a = v[ix[i]], b = v[ix[i + 1]], c = v[ix[i + 2]];
                var n = Vector3.Cross(b - a, c - a);
                float len = n.Length();
                if (len < 1e-6f) continue;
                n /= len;
                if (n.Z < 0) n = -n;                         // seen from above, either side of a surface faces up
                Triangle(a, b, c, 0.5f + 0.5f * Math.Max(0f, Vector3.Dot(n, light)));
            }
        }
        foreach (var ring in boxes)
        {
            if (ring.Length < 6) continue;
            var o = new Vector3(ring[0], ring[1], 0);
            for (int i = 2; i + 3 < ring.Length; i += 2)
                Triangle(o, new Vector3(ring[i], ring[i + 1], 0), new Vector3(ring[i + 2], ring[i + 3], 0), 0.62f);
        }

        float step = StepLine + (float)unitsPerPixel * 1.5f;     // a pitched roof's own slope between two pixels is not a step
        var px4 = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int at = y * w + x;
                if (float.IsNegativeInfinity(z[at])) continue;
                float s = lit[at];
                // a line where the building ends or its height steps
                bool Edge(int nx, int ny) => nx < 0 || ny < 0 || nx >= w || ny >= h ? false
                    : float.IsNegativeInfinity(z[ny * w + nx]) || MathF.Abs(z[ny * w + nx] - z[at]) > step;
                if (Edge(x - 1, y) || Edge(x + 1, y) || Edge(x, y - 1) || Edge(x, y + 1)) s *= 0.42f;
                int o = at * 4;
                px4[o] = (byte)Math.Clamp(196 * s, 0, 255); px4[o + 1] = (byte)Math.Clamp(214 * s, 0, 255); px4[o + 2] = (byte)Math.Clamp(226 * s, 0, 255); px4[o + 3] = 255;
            }
        return px4;
    }
}
