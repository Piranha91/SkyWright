using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HousecarlCore;
using SkyWright.Engine;
using Mutagen.Bethesda.Plugins;

namespace ViewProbe;

internal sealed record TerrainPatchFact(string Diffuse,
    (System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C)[] Triangles);
internal sealed record TerrainLayerFact(string Diffuse,
    (System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C,
     float Oa, float Ob, float Oc)[] Triangles);
internal sealed record LandHeightFact(int X, int Y, float[,] Heights,
    System.Numerics.Vector3[]? Normals = null, System.Numerics.Vector3[]? Colors = null);

/// <summary>
/// Complete owned output of the expensive record walks for one worklist cell. Every collection
/// and nested mutable array is copied at capture and again at application. No overlay getter,
/// lazy enumerable, callback, GL object or per-shot diagnostic belongs here.
/// </summary>
internal sealed class CellWalkFacts
{
    internal required FormKey CellKey { get; init; }
    internal required string CellLabel { get; init; }
    internal required bool Interior { get; init; }
    internal required FormKey TargetWorldspace { get; init; }
    internal required (int X, int Y)? TargetGrid { get; init; }

    internal required Dictionary<FormKey, PlacedRef> WinningRefs { get; init; }
    internal required Dictionary<FormKey, (FormKey Ws, PlacedRef Ref)> PersistentRefs { get; init; }
    /// <summary>References a later plugin took out of the loaded cells (WinningRefs.cs), so a retained walk reports them too.</summary>
    internal HashSet<FormKey> TakenOut { get; init; } = new();
    /// <summary>The ring-edge references the walk tracked (WinningRefs.cs), admitted per shot by MergeEdge.</summary>
    internal Dictionary<FormKey, (PlacedRef Ref, FormKey Cell)> EdgeRefs { get; init; } = new();
    /// <summary>The large-reference walk state (WinningRefs.cs LargeSnapshot), admitted per shot by MergeLarge.</summary>
    internal object? LargeState { get; init; }
    /// <summary>References whose last sighting is a deletion with no placement data, and the deleting plugin.</summary>
    internal Dictionary<FormKey, string> DeletedBy { get; init; } = new();
    internal required HashSet<FormKey> DisabledFlags { get; init; }
    internal required Dictionary<FormKey, (FormKey Parent, bool Opposite)> EnableParents { get; init; }
    internal required Dictionary<FormKey, string> RefEditorIds { get; init; }
    internal required Dictionary<FormKey, FormKey> RefCells { get; init; }
    internal required Dictionary<FormKey, PlaneHistory> PlaneHistories { get; init; }
    internal required string[] CellContestedBy { get; init; }
    internal required Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)> NpcRefs { get; init; }
    internal required Dictionary<FormKey, Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)>> NpcBases { get; init; }
    internal required Dictionary<FormKey, (FormKey Cell, string? CellEd, PlacedRef Ref)> Doors { get; init; }

    internal required NavmeshSelection.OwnedGraph Navigation { get; init; }
    internal required Dictionary<FormKey, string> BaseMeshes { get; init; }
    internal required HashSet<FormKey> MarkerBases { get; init; }
    internal required Dictionary<FormKey, string> BaseEditorIds { get; init; }
    internal required Dictionary<FormKey, string> BaseTypes { get; init; }
    internal required Dictionary<FormKey, (Vec3 Min, Vec3 Max)> BaseBounds { get; init; }
    internal required Dictionary<FormKey, FormKey> LtexToTxst { get; init; }
    internal required Dictionary<FormKey, string> LtexEditorIds { get; init; }
    internal required Dictionary<FormKey, string> TxstDiffuse { get; init; }
    /// <summary>TXST NormalOrGloss paths, for the native terrain's layer normal maps.</summary>
    internal Dictionary<FormKey, string> TxstNormal { get; init; } = new();
    internal required Dictionary<FormKey, (FormKey Mato, float MaxAngle)> BaseMato { get; init; }
    internal required Dictionary<FormKey, (string Ed, System.Numerics.Vector3 Projection, float Bias,
        float Scale, float UvScale, System.Numerics.Vector3 Color, bool SinglePass, string? Model,
        float NoiseUv)> Mato { get; init; }
    internal required Dictionary<FormKey, (string Ed, string? Noise, float NoiseUv,
        System.Numerics.Vector3 Shallow, System.Numerics.Vector3 Deep, System.Numerics.Vector3 Reflect,
        float Reflectivity, byte Opacity, float Fresnel,
        float FogNear, float FogFar, float FogAmount, float NoiseFalloff,
        System.Numerics.Vector3 Spec)> WaterRecords { get; init; }

    internal required Dictionary<(int X, int Y), (bool Has, float Height, FormKey? Water)> CellWater { get; set; }
    internal required FormKey? WorldWater { get; set; }
    internal required float WorldDefaultWaterHeight { get; set; }
    internal required Dictionary<(int X, int Y), (bool Has, float Height, FormKey? Water)> CellWaterTruth { get; set; }
    internal required FormKey? WorldWaterTruth { get; set; }
    internal required float WorldDefaultWaterHeightTruth { get; set; }
    internal required Dictionary<(int X, int Y), (FormKey Key, string? Ed)> CellsAtGrid { get; set; }
    internal required Dictionary<string, (float? Default, string? Parent)> WorldChain { get; set; }
    internal required string StationWorldspaceEditorId { get; set; }
    internal required Dictionary<(int X, int Y), Dictionary<FormKey, PlacedRef>> StationRefs { get; init; }
    internal required Dictionary<(int X, int Y), float[,]> StationLand { get; init; }
    /// <summary>The station floor's grids whose last LAND gives no heights (FloorFeed.FloorIndex), with the reason.</summary>
    internal Dictionary<(int X, int Y), string> StationUnreadableLand { get; init; } = new();
    /// <summary>The station floor's reach (FloorFeed.FloorReach): its filing is not changed after the walk.</summary>
    internal FloorFeed.FloorReach? StationReach { get; init; }

    internal required TerrainPatchFact[] TerrainPatches { get; set; }
    internal required TerrainLayerFact[] TerrainLayers { get; set; }
    internal required LandHeightFact[] LandHeights { get; set; }

    internal CellWalkFacts CopyForShot()
    {
        var nav = Navigation.Selection.CopyGraph(Navigation.Winners, Navigation.Versions);
        return new CellWalkFacts
        {
            CellKey = CellKey, CellLabel = CellLabel, Interior = Interior,
            TargetWorldspace = TargetWorldspace, TargetGrid = TargetGrid,
            WinningRefs = WinningRefs.ToDictionary(x => x.Key, x => Copy(x.Value)),
            PersistentRefs = PersistentRefs.ToDictionary(x => x.Key, x => (x.Value.Ws, Copy(x.Value.Ref))),
            TakenOut = TakenOut.ToHashSet(),
            EdgeRefs = EdgeRefs.ToDictionary(x => x.Key, x => (Copy(x.Value.Ref), x.Value.Cell)),
            LargeState = LargeState,   // never mutated: RestoreLarge copies out of it
            DeletedBy = new(DeletedBy),
            DisabledFlags = DisabledFlags.ToHashSet(), EnableParents = new(EnableParents),
            RefEditorIds = new(RefEditorIds), RefCells = new(RefCells),
            PlaneHistories = PlaneHistories.ToDictionary(x => x.Key, x => x.Value.Copy()),
            CellContestedBy = CellContestedBy.ToArray(),
            NpcRefs = NpcRefs.ToDictionary(x => x.Key, x => (x.Value.Cell, x.Value.CellEd, Copy(x.Value.Ref))),
            NpcBases = NpcBases.ToDictionary(x => x.Key, x => x.Value.ToDictionary(y => y.Key,
                y => (y.Value.Cell, y.Value.CellEd, Copy(y.Value.Ref)))),
            Doors = Doors.ToDictionary(x => x.Key, x => (x.Value.Cell, x.Value.CellEd, Copy(x.Value.Ref))),
            Navigation = nav,
            BaseMeshes = new(BaseMeshes), MarkerBases = MarkerBases.ToHashSet(),
            BaseEditorIds = new(BaseEditorIds), BaseTypes = new(BaseTypes), BaseBounds = new(BaseBounds),
            LtexToTxst = new(LtexToTxst), LtexEditorIds = new(LtexEditorIds), TxstDiffuse = new(TxstDiffuse), TxstNormal = new(TxstNormal),
            BaseMato = new(BaseMato), Mato = new(Mato), WaterRecords = new(WaterRecords),
            CellWater = new(CellWater), WorldWater = WorldWater,
            WorldDefaultWaterHeight = WorldDefaultWaterHeight,
            CellWaterTruth = new(CellWaterTruth), WorldWaterTruth = WorldWaterTruth,
            WorldDefaultWaterHeightTruth = WorldDefaultWaterHeightTruth,
            CellsAtGrid = new(CellsAtGrid), WorldChain = new(WorldChain, StringComparer.OrdinalIgnoreCase),
            StationWorldspaceEditorId = StationWorldspaceEditorId,
            StationRefs = StationRefs.ToDictionary(x => x.Key,
                x => x.Value.ToDictionary(y => y.Key, y => Copy(y.Value))),
            StationLand = StationLand.ToDictionary(x => x.Key, x => (float[,])x.Value.Clone()),
            StationUnreadableLand = new(StationUnreadableLand),
            StationReach = StationReach,   // shared: its filing is not changed after the walk
            TerrainPatches = TerrainPatches.Select(x => new TerrainPatchFact(x.Diffuse, x.Triangles.ToArray())).ToArray(),
            TerrainLayers = TerrainLayers.Select(x => new TerrainLayerFact(x.Diffuse, x.Triangles.ToArray())).ToArray(),
            LandHeights = LandHeights.Select(x => new LandHeightFact(x.X, x.Y, (float[,])x.Heights.Clone(),
                x.Normals?.ToArray(), x.Colors?.ToArray())).ToArray(),
        };
    }

    internal static PlacedRef Copy(PlacedRef r) => r with
    {
        LinkedReferences = r.LinkedReferences?.ToArray(),
        ActivateParents = r.ActivateParents?.ToArray(),
    };

    internal static CellWalkFacts SelfTestFixture(string label)
    {
        var nav = new NavmeshSelection(FormKey.Null, Array.Empty<string>(), Array.Empty<string>());
        var graph = nav.CopyGraph(new Dictionary<FormKey, NavmeshGeometry>(),
            new Dictionary<string, Dictionary<FormKey, NavmeshGeometry>>(StringComparer.OrdinalIgnoreCase));
        return new CellWalkFacts
        {
            CellKey = FormKey.Null, CellLabel = label, Interior = true,
            TargetWorldspace = FormKey.Null, TargetGrid = null,
            WinningRefs = new(), PersistentRefs = new(), DisabledFlags = new(), EnableParents = new(),
            RefEditorIds = new(), RefCells = new(), PlaneHistories = new(), CellContestedBy = Array.Empty<string>(),
            NpcRefs = new(), NpcBases = new(), Doors = new(), Navigation = graph,
            BaseMeshes = new(), MarkerBases = new(), BaseEditorIds = new(), BaseTypes = new(), BaseBounds = new(),
            LtexToTxst = new(), LtexEditorIds = new(), TxstDiffuse = new(), BaseMato = new(), Mato = new(),
            WaterRecords = new(), CellWater = new(), WorldWater = null,
            WorldDefaultWaterHeight = float.MaxValue, CellWaterTruth = new(), WorldWaterTruth = null,
            WorldDefaultWaterHeightTruth = float.MaxValue, CellsAtGrid = new(),
            WorldChain = new(StringComparer.OrdinalIgnoreCase), StationWorldspaceEditorId = "",
            StationRefs = new(), StationLand = new(), TerrainPatches = Array.Empty<TerrainPatchFact>(),
            TerrainLayers = Array.Empty<TerrainLayerFact>(), LandHeights = Array.Empty<LandHeightFact>(),
        };
    }
}

internal static class CellFactsOwnership
{
    internal static void Validate(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(root, "facts", seen);
    }

    static void Visit(object? value, string path, HashSet<object> seen)
    {
        if (value is null) return;
        var type = value.GetType();
        if (type.Namespace?.StartsWith("Mutagen.Bethesda.Skyrim", StringComparison.Ordinal) == true)
            throw new InvalidOperationException($"prepared cell contains a Mutagen getter at {path}: {type.FullName}");
        if (value is Delegate) throw new InvalidOperationException($"prepared cell contains a callback at {path}");
        if (type.IsPrimitive || type.IsEnum || value is string || value is decimal
            || type == typeof(FormKey) || type == typeof(ModKey)) return;
        if (!type.IsValueType && !seen.Add(value)) return;
        if (value is IEnumerable sequence)
        {
            if (value is not Array && !type.IsGenericType)
                throw new InvalidOperationException($"prepared cell contains an unowned enumerable at {path}: {type.FullName}");
            int i = 0; foreach (var item in sequence) Visit(item, $"{path}[{i++}]", seen);
            return;
        }
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType.Namespace?.StartsWith("Mutagen.Bethesda.Skyrim", StringComparison.Ordinal) == true)
                throw new InvalidOperationException($"prepared cell declares a Mutagen getter at {path}.{field.Name}: {field.FieldType.FullName}");
            Visit(field.GetValue(value), path + "." + field.Name, seen);
        }
    }

    internal static string Key(params IEnumerable<string>[] parts)
    {
        var text = string.Join("\n", parts.SelectMany(x => x));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    sealed class GetterShapedContamination
    {
        public Mutagen.Bethesda.Skyrim.ISkyrimMajorRecordGetter? Getter;
    }

    internal static bool SelfTest()
    {
        Validate(new[] { new CellBoundsFact(new Vec3(1, 2, 3), new Vec3(4, 5, 6)) });
        bool getterRefused = false, callbackRefused = false;
        try { Validate(new GetterShapedContamination()); }
        catch (InvalidOperationException e) { getterRefused = e.Message.Contains("Mutagen getter", StringComparison.Ordinal); }
        try { Validate(new Func<int>(() => 1)); }
        catch (InvalidOperationException e) { callbackRefused = e.Message.Contains("callback", StringComparison.Ordinal); }
        return getterRefused && callbackRefused;
    }
}

/// <summary>One guard for the process-owned asset and BOS readers, intentionally outside every
/// cell lease. It binds file paths and observed size/write-time metadata, so a later cell cannot
/// reuse process state after a detectable profile/plugin/rule input change.</summary>
internal sealed class PreparedProcessIdentity
{
    string? _bound;

    internal bool TryBind(string identity, out string? refusal)
    {
        if (_bound is null) { _bound = identity; refusal = null; return true; }
        if (StringComparer.Ordinal.Equals(_bound, identity)) { refusal = null; return true; }
        refusal = "--reuse-cell refused: a profile, plugin or BOS rule input changed while process-owned readers were live";
        return false;
    }

    internal static string FileToken(string path)
    {
        string full = Path.GetFullPath(path);
        var fi = new FileInfo(full);
        return fi.Exists ? $"{full}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : full + "|MISSING";
    }

    internal static bool ChangedFileAcrossCellOwnersSelfTest()
    {
        string path = Path.Combine(Path.GetTempPath(), "viewprobe-process-identity-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "A");
            var guard = new PreparedProcessIdentity();
            string a = CellFactsOwnership.Key(new[] { FileToken(path) });
            CellScene firstOwner;
            bool first;
            using (firstOwner = new CellScene("ProcessIdentityOne"))
                first = guard.TryBind(a, out _);
            File.AppendAllText(path, "-changed");
            string b = CellFactsOwnership.Key(new[] { FileToken(path) });
            using var secondOwner = new CellScene("ProcessIdentityTwo");
            bool refused = !guard.TryBind(b, out var refusal);
            return first && a != b && !ReferenceEquals(firstOwner, secondOwner) && refused
                && refusal?.Contains("process-owned", StringComparison.Ordinal) == true;
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
