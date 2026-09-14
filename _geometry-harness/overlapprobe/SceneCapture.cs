using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using HarnessShared;
using HousecarlCore;

static class SceneCapture
{
    internal const double ExteriorContextMargin = 4096d;

    internal sealed record AssetProviderCheck(string CandidateName, string Mo2Name, int Mo2Priority,
        bool Enabled, string Directory);
    internal sealed record AssetPreflight(string CandidatePath, string BaseProfile,
        IReadOnlyList<AssetProviderCheck> Providers, int ProfileProviderCount, int OmittedProviderCount);

    internal enum ExteriorMembership { InNeighbourhood, OutsideNeighbourhood, Unresolved }
    internal sealed record ExteriorDisposition(ExteriorMembership Membership, bool AdmitToScene,
        bool RetainUnassessed, string ScopeReason);

    internal sealed record ImportedSeed(FormKey Reference, FormKey Cell, FormKey Space,
        double X, double Y, double Z, bool Interior, Aabb? WorldBounds, string? BoundsReason, string Provenance);
    internal sealed record ImportedScope(string Path, string Sha256, IReadOnlySet<FormKey> References,
        IReadOnlySet<FormKey> Cells, IReadOnlySet<string> PairKeys, IReadOnlyList<ImportedSeed> Seeds);

    internal static ImportedScope ReadScope(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        var refs = new HashSet<FormKey>();
        var cells = new HashSet<FormKey>();
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        var seeds = new List<ImportedSeed>();
        if (!doc.RootElement.TryGetProperty("scene", out var scene)
            || !scene.TryGetProperty("discovery", out var discovery)
            || !discovery.TryGetProperty("seedReferences", out var seedRows) || seedRows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("scene scope has no scene.discovery.seedReferences[] receipt");
        var declaredSeeds = new HashSet<FormKey>();
        bool seedDefinitionsProvided = discovery.TryGetProperty("seedDefinitions", out var seedDefinitions);
        if (seedDefinitionsProvided && seedDefinitions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("scene scope scene.discovery.seedDefinitions must be an array");
        bool hasSeedDefinitions = seedDefinitionsProvided;
        foreach (var seed in seedRows.EnumerateArray())
        {
            if (!TryForm(seed, out var key)) throw new InvalidDataException("scene scope has a malformed seed reference");
            declaredSeeds.Add(key);
        }
        if (declaredSeeds.Count == 0) throw new InvalidDataException("scene scope seedReferences[] is empty");
        if (!doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("scene scope has no records[] array");
        if (!doc.RootElement.TryGetProperty("pairs", out var pairRows) || pairRows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("scene scope has no pairs[] array");
        if (records.GetArrayLength() == 0 && !hasSeedDefinitions)
            throw new InvalidDataException("scene scope records[] is empty; an empty population is not a reusable scope");
        foreach (var row in records.EnumerateArray())
        {
            if (!row.TryGetProperty("formid", out var formid) || !TryForm(formid, out var key))
                throw new InvalidDataException("scene scope record has a missing or malformed formid");
            refs.Add(key);
            if (!row.TryGetProperty("fields", out var fields)
                || !fields.TryGetProperty("cell", out var cell) || cell.ValueKind != JsonValueKind.Object
                || !cell.TryGetProperty("formid", out var cellId) || !TryForm(cellId, out key))
                throw new InvalidDataException($"scene scope record {formid.GetString()} has no canonical fields.cell.formid");
            var cellKey = key;
            cells.Add(cellKey);
            if (!fields.TryGetProperty("space", out var spaceId) || !TryForm(spaceId, out var space)
                || !fields.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.Array
                || position.GetArrayLength() != 3 || !position.EnumerateArray().All(v => v.TryGetDouble(out _)))
                throw new InvalidDataException($"scene scope record {formid.GetString()} has no finite canonical space/position");
            var xyz = position.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (xyz.Any(v => !double.IsFinite(v)))
                throw new InvalidDataException($"scene scope record {formid.GetString()} has a non-finite position");
            bool interior = fields.GetProperty("cell").TryGetProperty("interior", out var intValue) && intValue.ValueKind == JsonValueKind.True;
            var recordKey = FormKey.Factory(formid.GetString()!);
            Aabb? bounds = null;
            string? boundsReason = null;
            if (row.TryGetProperty("geometry", out var geometry)
                && geometry.TryGetProperty("worldBounds", out var worldBounds) && worldBounds.ValueKind == JsonValueKind.Object
                && TryVector(worldBounds, "min", out var min) && TryVector(worldBounds, "max", out var max))
                bounds = new Aabb(new((float)min[0], (float)min[1], (float)min[2]), new((float)max[0], (float)max[1], (float)max[2]), false);
            else boundsReason = "imported seed has no finite placed world bounds";
            if (!hasSeedDefinitions && declaredSeeds.Contains(recordKey)) seeds.Add(new(recordKey, cellKey, space, xyz[0], xyz[1], xyz[2], interior, bounds, boundsReason, "effective-record"));
        }
        foreach (var row in pairRows.EnumerateArray())
        {
            if (!row.TryGetProperty("pairKey", out var pair) || pair.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("scene scope pair has no pairKey");
            var pairKey = pair.GetString()!;
            if (!row.TryGetProperty("a", out var a) || !TryForm(a, out var ak)
                || !row.TryGetProperty("b", out var b) || !TryForm(b, out var bk))
                throw new InvalidDataException($"scene scope pair {pairKey} has a missing or malformed endpoint");
            var canonical = CanonicalPair(ak, bk);
            if (!string.Equals(pairKey, canonical, StringComparison.Ordinal))
                throw new InvalidDataException($"scene scope pairKey is not canonical or does not match its endpoints: {pairKey}");
            pairs.Add(pairKey); refs.Add(ak); refs.Add(bk);
        }
        if (hasSeedDefinitions)
        {
            int definitionIndex = 0;
            foreach (var definition in seedDefinitions.EnumerateArray())
            {
                definitionIndex++;
                if (!definition.TryGetProperty("formid", out var formid) || !TryForm(formid, out var reference)
                    || !declaredSeeds.Contains(reference)
                    || !definition.TryGetProperty("cell", out var cell) || !TryForm(cell, out var cellKey)
                    || !definition.TryGetProperty("space", out var space) || !TryForm(space, out var spaceKey)
                    || !definition.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.Array
                    || position.GetArrayLength() != 3 || !position.EnumerateArray().All(v => v.TryGetDouble(out _)))
                    throw new InvalidDataException($"scene scope seedDefinitions[{definitionIndex - 1}] has malformed identity, cell, space, or position");
                var xyz = position.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (xyz.Any(v => !double.IsFinite(v)))
                    throw new InvalidDataException($"scene scope seedDefinitions[{definitionIndex - 1}] has a non-finite position");
                Aabb? bounds = null;
                if (!definition.TryGetProperty("worldBounds", out var worldBounds))
                    throw new InvalidDataException($"scene scope seedDefinitions[{definitionIndex - 1}] has no worldBounds member");
                if (worldBounds.ValueKind == JsonValueKind.Object)
                {
                    if (!TryVector(worldBounds, "min", out var min) || !TryVector(worldBounds, "max", out var max))
                        throw new InvalidDataException($"scene scope seedDefinitions[{definitionIndex - 1}] has malformed worldBounds");
                    bounds = new Aabb(new((float)min[0], (float)min[1], (float)min[2]), new((float)max[0], (float)max[1], (float)max[2]), false);
                }
                else if (worldBounds.ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException($"scene scope seedDefinitions[{definitionIndex - 1}] worldBounds must be an object or null");
                if (!definition.TryGetProperty("interior", out var iv) || iv.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException($"scene scope seedDefinitions[{definitionIndex - 1}] has no boolean interior");
                bool interior = iv.ValueKind == JsonValueKind.True;
                if (!definition.TryGetProperty("boundsReason", out var br) || br.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new InvalidDataException($"scene scope seedDefinitions[{definitionIndex - 1}] boundsReason must be a string or null");
                string? reason = br.ValueKind == JsonValueKind.String ? br.GetString() : null;
                if (!definition.TryGetProperty("provenance", out var pv) || pv.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pv.GetString()))
                    throw new InvalidDataException($"scene scope seedDefinitions[{definitionIndex - 1}] has no provenance");
                string provenance = pv.GetString()!;
                seeds.Add(new(reference, cellKey, spaceKey, xyz[0], xyz[1], xyz[2], interior, bounds, reason, provenance));
            }
        }
        var missingSeeds = declaredSeeds.Except(seeds.Select(s => s.Reference)).ToHashSet();
        if (missingSeeds.Count > 0)
            throw new InvalidDataException("scene scope seed reference(s) lack persisted definitions: " + string.Join(", ", missingSeeds));
        return new(Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), refs, cells, pairs, seeds);
    }

    /// <summary>
    /// Validates the asset half of a source-scene candidate without resolving or hashing its plugin order.
    /// A candidate may deliberately select a subset of the profile's providers, but every selected folder
    /// must be a real MO2 provider and the selected providers must retain their strongest-first priority.
    /// </summary>
    internal static AssetPreflight PreflightCandidateAssets(string path, string profileName, string profileDir, string modsDir)
    {
        var candidate = CandidateFile.Read(path);
        if (!string.Equals(candidate.BaseProfile, profileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"candidate BaseProfile '{candidate.BaseProfile}' does not match --profile '{profileName}'");
        if (candidate.Mods is null)
            throw new InvalidDataException("scene capture requires the candidate file's explicit Mods list; profile assets are not an identified source scene");

        var profileRows = new List<(string Name, bool Enabled)>();
        string modlist = Path.Combine(profileDir, "modlist.txt");
        if (!File.Exists(modlist)) throw new InvalidDataException($"MO2 provider list not found: {modlist}");
        foreach (var raw in File.ReadAllLines(modlist))
        {
            var line = raw.TrimEnd();
            if (line.Length < 2 || line[0] is not ('+' or '-')) continue;
            var name = line[1..].Trim();
            if (name.Length == 0 || name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            profileRows.Add((name, line[0] == '+'));
        }

        var byName = profileRows.Select((row, index) => (row, index)).ToDictionary(
            x => x.row.Name, x => (x.index, x.row.Enabled), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<AssetProviderCheck>(candidate.Mods.Count);
        int previousPriority = -1;
        foreach (var requested in candidate.Mods)
        {
            if (string.IsNullOrWhiteSpace(requested))
                throw new InvalidDataException("candidate Mods contains an empty provider name");
            if (!seen.Add(requested))
                throw new InvalidDataException($"candidate Mods lists provider '{requested}' more than once");
            if (!byName.TryGetValue(requested, out var profile))
                throw new InvalidDataException($"candidate Mods names '{requested}', but the base profile's modlist.txt has no provider by that name (possible typo)");
            if (profile.index <= previousPriority)
                throw new InvalidDataException($"candidate Mods does not retain MO2 strongest-first priority at '{requested}'");
            previousPriority = profile.index;
            string canonical = profileRows[profile.index].Name;
            string directory = Path.Combine(modsDir, canonical);
            if (!Directory.Exists(directory))
                throw new InvalidDataException($"candidate Mods names MO2 provider '{canonical}', but its mod folder is missing: {directory}");
            checks.Add(new(requested, canonical, profile.index, profile.Enabled, Path.GetFullPath(directory)));
        }
        return new(Path.GetFullPath(path), candidate.BaseProfile, checks, profileRows.Count,
            profileRows.Count - checks.Count);
    }

    internal static ExteriorMembership ExteriorUnknownMembership(FormKey space, double x, double y, double z,
        IReadOnlyList<ImportedSeed> exteriorSeeds)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return ExteriorMembership.Unresolved;
        var sameSpace = exteriorSeeds.Where(s => !s.Interior && s.Space == space).ToList();
        if (sameSpace.Count == 0) return ExteriorMembership.Unresolved;
        bool hasKnownFootprint = false;
        bool hasUnknownFootprint = false;
        foreach (var seed in sameSpace)
        {
            if (seed.WorldBounds is not { IsEmpty: false } bounds || !SpatialOverlap.IsFinite(bounds))
            { hasUnknownFootprint = true; continue; }
            hasKnownFootprint = true;
            if (x >= bounds.Min.X - ExteriorContextMargin && x <= bounds.Max.X + ExteriorContextMargin
                && y >= bounds.Min.Y - ExteriorContextMargin && y <= bounds.Max.Y + ExteriorContextMargin
                && z >= bounds.Min.Z - ExteriorContextMargin && z <= bounds.Max.Z + ExteriorContextMargin)
                return ExteriorMembership.InNeighbourhood;
        }
        return hasUnknownFootprint || !hasKnownFootprint
            ? ExteriorMembership.Unresolved
            : ExteriorMembership.OutsideNeighbourhood;
    }

    internal static ExteriorDisposition ExteriorUnknownDisposition(FormKey space, double x, double y, double z,
        IReadOnlyList<ImportedSeed> exteriorSeeds)
    {
        var membership = ExteriorUnknownMembership(space, x, y, z, exteriorSeeds);
        return membership switch
        {
            ExteriorMembership.InNeighbourhood => new(membership, true, false,
                "placed origin lies inside the existing 4096-unit scene search envelope"),
            ExteriorMembership.OutsideNeighbourhood => new(membership, false, true,
                "placed origin is outside the existing 4096-unit scene search envelope; geometry extent is unavailable, so physical scene membership is unassessed"),
            _ => new(membership, false, true,
                "strict render geometry and a localizing seed footprint are unavailable, so physical scene membership is unassessed")
        };
    }

    internal static IReadOnlySet<string> ReadPairKeys(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("pairs", out var pairs) && pairs.ValueKind == JsonValueKind.Array ? pairs
            : throw new InvalidDataException("contact evidence input has no pairs[] array");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("pairKey", out var pair) || pair.ValueKind != JsonValueKind.String
                || !row.TryGetProperty("a", out var a) || !TryForm(a, out var ak)
                || !row.TryGetProperty("b", out var b) || !TryForm(b, out var bk))
                throw new InvalidDataException("contact evidence pair has missing/malformed pairKey/a/b");
            var canonical = CanonicalPair(ak, bk);
            if (!string.Equals(pair.GetString(), canonical, StringComparison.Ordinal))
                throw new InvalidDataException($"contact evidence pair is noncanonical: {pair.GetString()}");
            result.Add(canonical);
        }
        return result;
    }

    private static bool TryForm(JsonElement value, out FormKey key)
    {
        key = FormKey.Null;
        if (value.ValueKind != JsonValueKind.String) return false;
        try { key = FormKey.Factory(value.GetString()!); return true; }
        catch { return false; }
    }

    private static bool TryVector(JsonElement parent, string name, out double[] value)
    {
        value = Array.Empty<double>();
        if (!parent.TryGetProperty(name, out var row) || row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 3) return false;
        value = row.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        return value.All(double.IsFinite);
    }

    private static string CanonicalPair(FormKey a, FormKey b)
    {
        string x = a.ToString(), y = b.ToString();
        return string.CompareOrdinal(x, y) <= 0 ? x + "||" + y : y + "||" + x;
    }

}
