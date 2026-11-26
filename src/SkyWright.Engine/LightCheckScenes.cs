using System.Numerics;
using HousecarlCore;

namespace SkyWright.Engine;

// THE LIGHTING CHECK's population (2026-11-26, Gate B): the API its known answers are written against. Not yet implemented.

public sealed record LightCheckPlacement(string Key, string? Cell, string? Worldspace, Vector3 Pos, float Reach, IReadOnlyList<string> Touchers, bool IsLight);

public sealed record LightCheckSceneKey(bool Exterior, string Space, int X = 0, int Y = 0)
{
    public string Id => Exterior ? $"{Space}@{X},{Y}" : Space;
}

public sealed class LightCheckSceneIndex
{
    public const float Cell = 4096f;
    public LightCheckSceneIndex(IEnumerable<LightCheckPlacement> placements) { }
    public static int Grid(float v) => (int)Math.Floor(v / Cell);
    public static IEnumerable<(int X, int Y)> TerrainGrids(LightCheckSceneKey scene) => Enumerable.Empty<(int, int)>();
    public IReadOnlyList<(LightCheckPlacement P, bool Subject)> Members(LightCheckSceneKey scene) => Array.Empty<(LightCheckPlacement, bool)>();
    public IReadOnlyList<LightCheckSceneKey> TwoSide(LightCheckSides sides, out int oneSide, out int noLight) { oneSide = noLight = 0; return Array.Empty<LightCheckSceneKey>(); }
    public static TriangleSoup TerrainSoup(int gx, int gy, float[,] heights) => new(Array.Empty<Vector3>(), Array.Empty<int>());
    public static LightCheckSides? Sides(IReadOnlyList<string> dependencies, IReadOnlyList<string> selection, IReadOnlyList<string> rest,
                                         IReadOnlyList<string> combined, IReadOnlyDictionary<string, string> modByPlugin, out string? refusal)
    { refusal = null; return new LightCheckSides(new Dictionary<string, LightSide>(), modByPlugin); }
}
