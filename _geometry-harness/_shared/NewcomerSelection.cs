using System.Text.Json;
using System.Text.Json.Serialization;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HarnessShared;

// NEWCOMER SELECTION — which installed plugins one new mod should be evaluated against (roadmap 4.26, part 1).
//
// An end user has a working order and one mod to add. Before any pair evaluation runs, something has to say which
// installed plugins the newcomer meets. This file is that step: cheap records-and-files evidence, no geometry, no
// bounds (ObjectBounds are unreliable and prune nothing here), erring toward inclusion — a plugin it leaves out is
// never looked at again.
//
// EVERY PIECE OF EVIDENCE IS PAIRWISE: a fact about the newcomer N and one installed plugin B, never about which
// plugin wins the order. So selecting many newcomers against one indexed order gives exactly the answer of selecting
// each alone (the self-test checks it).
//
// SELECTING kinds — any one puts B on the candidate list:
//   records     a FormKey both carry, except the CONTAINER records any worldspace or navmesh edit drags along (the
//               WRLD record, a worldspace's persistent CELL record, NAVI);
//   cells       the same interior cell, or the same exterior cell (worldspace + grid). A persistent reference counts in
//               the grid cell its position falls in, not in the gridless persistent cell;
//   neighbours  an exterior cell of B edge- or corner-adjacent (ring 1) to one of N's, in the same worldspace;
//   assets      a Data-relative path N provides (loose, or in its BSA) that B's mod folder also provides (loose, or in
//               a BSA the order loads). Folder-root files and fomod/ are not assets;
//   baseLinks   N places a reference whose base record B carries, or B places a reference whose base N carries.
// NOTED kinds — counted for every pair, never a reason to select: container-only sharing, ring-2 neighbours, shared
// non-base masters, and form links outside placement bases (N's records pointing at records B carries; with
// reverse links on, B's pointing at N's).
//
// NOT CANDIDATES: N's masters (dependencies); plugins that master N (removed from the order with N, transitively);
// plugins inside N's folder that master a non-base plugin outside it ("packaged patches": set aside, never
// evidence); plugins the game's Data folder provides (base).

public sealed class Footprint
{
    public string Name = "", Path = "";
    public string? Error;
    public readonly HashSet<FormKey> Keys = new();
    public readonly HashSet<FormKey> Containers = new();
    public readonly HashSet<string> Interior = new(StringComparer.Ordinal);            // interior cell FormKey strings
    public readonly HashSet<(FormKey Ws, int X, int Y)> Exterior = new();
    public readonly HashSet<FormKey> PlacedBases = new();
    public HashSet<FormKey>? Links;                                                     // null: not read
    public Dictionary<FormKey, (string Type, string? Edid)>? Info;                      // newcomer only: display
    public Dictionary<string, string>? CellNames;                                       // newcomer only: display
    public int NewRecords, Overrides, ScriptCarriers;
    public IReadOnlyList<string> Masters = Array.Empty<string>();

    public const float CellSize = 4096f;
    public static int Grid(float v) => (int)Math.Floor(v / CellSize);

    /// <summary>Read one plugin's footprint through the core's overlay open. Values are copied out; the overlay is
    /// disposed before returning. A plugin that will not open or walk comes back with <see cref="Error"/> set and its
    /// partial sets cleared, so a half-read plugin never reads as "nothing in common".</summary>
    public static Footprint Read(string path, string? dataDir, bool links, bool info)
    {
        var fp = new Footprint { Name = System.IO.Path.GetFileName(path), Path = path };
        if (info) { fp.Info = new(); fp.CellNames = new(StringComparer.Ordinal); }
        ISkyrimModGetter mod;
        try { mod = LoadOrderResolver.OpenOverlay(path, dataDir); }
        catch (Exception ex) { fp.Error = $"would not open — {Concise(ex)}"; return fp; }
        try
        {
            fp.Masters = mod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
            var own = mod.ModKey;
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                var k = rec.FormKey;
                fp.Keys.Add(k);
                if (k.ModKey == own) fp.NewRecords++; else fp.Overrides++;
                if (rec is IWorldspaceGetter || rec is INavigationMeshInfoMapGetter) fp.Containers.Add(k);
                if (rec is IPlacedObjectGetter po) { if (po.Base.FormKeyNullable is { IsNull: false } b) fp.PlacedBases.Add(b); }
                else if (rec is IPlacedNpcGetter pn) { if (pn.Base.FormKeyNullable is { IsNull: false } b) fp.PlacedBases.Add(b); }
                if (rec is IHaveVirtualMachineAdapterGetter vm && vm.VirtualMachineAdapter is not null) fp.ScriptCarriers++;
                if (links)
                {
                    fp.Links ??= new();
                    foreach (var l in rec.EnumerateFormLinks()) if (!l.FormKey.IsNull && l.FormKey != k) fp.Links.Add(l.FormKey);
                }
                if (info) fp.Info![k] = (rec.GetType().Name.Replace("BinaryOverlay", ""), string.IsNullOrEmpty(rec.EditorID) ? null : rec.EditorID);
            }
            foreach (var ws in mod.Worldspaces.Records)
            {
                if (ws.TopCell is { } tc)
                {
                    fp.Containers.Add(tc.FormKey);                                  // the persistent cell's own CELL record
                    foreach (var r in tc.Persistent.Concat(tc.Temporary))
                        if (r is IPlacedGetter pg && pg.Placement is { } pl)
                            fp.Exterior.Add((ws.FormKey, Grid(pl.Position.X), Grid(pl.Position.Y)));
                }
                foreach (var blk in ws.SubCells)
                    foreach (var sub in blk.Items)
                        foreach (var c in sub.Items)
                        {
                            if (c.Grid?.Point is { } gp)
                            {
                                fp.Exterior.Add((ws.FormKey, gp.X, gp.Y));
                                if (info && !string.IsNullOrEmpty(c.EditorID)) fp.CellNames![ExtKey(ws.FormKey, gp.X, gp.Y)] = c.EditorID!;
                            }
                            else fp.Interior.Add(c.FormKey.ToString());              // a gridless exterior cell: by identity
                        }
            }
            foreach (var blk in mod.Cells.Records)
                foreach (var sub in blk.SubBlocks)
                    foreach (var c in sub.Cells)
                    {
                        fp.Interior.Add(c.FormKey.ToString());
                        if (info && !string.IsNullOrEmpty(c.EditorID)) fp.CellNames![c.FormKey.ToString()] = c.EditorID!;
                    }
        }
        catch (Exception ex)
        {
            fp.Error = $"would not walk — {Concise(ex)}";
            fp.Keys.Clear(); fp.Containers.Clear(); fp.Interior.Clear(); fp.Exterior.Clear(); fp.PlacedBases.Clear(); fp.Links?.Clear();
        }
        finally { (mod as IDisposable)?.Dispose(); }
        return fp;
    }

    public static string ExtKey(FormKey ws, int x, int y) => $"{ws} ({x},{y})";

    public static string Concise(Exception ex)
    {
        var s = ex.Message.Replace("\r", "").Replace("\n", " | ").Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}

/// <summary>Data-relative asset paths, normalised: lower case, backslashes.</summary>
public static class AssetPaths
{
    public static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>The loose asset paths under one mod folder: every file below a subfolder, except fomod/. A file at the
    /// folder root (a plugin, a BSA, a readme, meta.ini) is not a Data asset.</summary>
    public static IEnumerable<string> Loose(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if (System.IO.Path.GetFileName(sub).Equals("fomod", StringComparison.OrdinalIgnoreCase)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files) yield return Norm(System.IO.Path.GetRelativePath(folder, f));
        }
    }

    /// <summary>One BSA's file table, normalised. Throws when the archive cannot be read; the caller names it.</summary>
    public static IEnumerable<string> Archive(string archivePath)
    {
        var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        var list = new List<string>();
        foreach (var file in reader.Files) list.Add(Norm(file.Path));
        return list;
    }

    /// <summary>The BSAs at a folder's root that load with one of the given plugins: "Plugin.bsa" or "Plugin - X.bsa".</summary>
    public static IEnumerable<string> ArchivesFor(string folder, IEnumerable<string> pluginNames)
    {
        if (!Directory.Exists(folder)) yield break;
        var stems = pluginNames.Select(n => System.IO.Path.GetFileNameWithoutExtension(n)).ToList();
        foreach (var f in Directory.EnumerateFiles(folder, "*.bsa"))
        {
            var s = System.IO.Path.GetFileNameWithoutExtension(f);
            if (stems.Any(st => s.Equals(st, StringComparison.OrdinalIgnoreCase) || s.StartsWith(st + " - ", StringComparison.OrdinalIgnoreCase)))
                yield return f;
        }
    }
}

// ----------------------------------------------------------------------------------------------------------------
//  The output shapes
// ----------------------------------------------------------------------------------------------------------------

public sealed record SelEvidence(int Records, IReadOnlyList<string> RecordExamples, int Cells, IReadOnlyList<string> CellExamples,
                                 int Neighbours, IReadOnlyList<string> NeighbourExamples, int Assets, IReadOnlyList<string> AssetExamples,
                                 int NewcomerPlacesTheirBase, int TheyPlaceNewcomerBase, IReadOnlyList<string> BaseLinkExamples);
public sealed record SelNoted(int ContainerRecords, int Ring2Cells, IReadOnlyList<string> SharedMasters, int FormLinks, int? ReverseFormLinks);
public sealed record SelCandidate(string Plugin, string? Folder, int Rank, IReadOnlyList<string> Kinds, SelEvidence Evidence, SelNoted Noted);
public sealed record SelExcluded(string Plugin, string? Folder, string Reason, SelNoted? Noted);
public sealed record SelNamed(string Plugin, string Why);
public sealed record SelAssetOnly(string Folder, int Assets, IReadOnlyList<string> Examples);
public sealed record SelNewcomerPlugin(string Name, string Path, string Sha256, IReadOnlyList<string> Masters, int NewRecords, int Overrides);
public sealed record SelNewcomer(string? Folder, IReadOnlyList<SelNewcomerPlugin> Plugins, IReadOnlyList<SelNamed> PackagedPatches,
                                 int Records, int InteriorCells, int ExteriorCells, int Assets, int LooseAssets, int ArchiveAssets,
                                 IReadOnlyList<string> Archives, int ScriptCarriers, IReadOnlyList<string> RuntimeLayerFiles, int ScriptFiles);
public sealed record SelOrder(string Source, string Identity, int Plugins, IReadOnlyList<SelNamed> Removed);
public sealed record SelRule(int Ring, IReadOnlyList<string> Containers, IReadOnlyList<string> Selecting, IReadOnlyList<string> Noted, bool ReverseLinks, string Ranking);
public sealed record Selection(
    string Tool, int Schema, SelNewcomer Newcomer, SelOrder Order, IReadOnlyList<SelNamed> Dependencies,
    IReadOnlyList<SelCandidate> Candidates, IReadOnlyList<SelAssetOnly> AssetOnlyFolders, IReadOnlyList<SelExcluded> Excluded,
    IReadOnlyList<SelNamed> Unreadable, bool Incomplete, IReadOnlyList<string> Unmeasured, SelRule Rule,
    IReadOnlyDictionary<string, object?>? Cost);

/// <summary>The newcomer's own facts the rule reads: its footprint (all its plugins together), its asset paths and its
/// non-base masters.</summary>
public sealed record NewcomerFacts(Footprint Fp, HashSet<string> Assets, HashSet<string> NonBaseMasters,
                                   HashSet<string> Plugins, string? FolderPath);

/// <summary>One installed plugin as the rule sees it.</summary>
public sealed record InstalledFacts(string Name, string? Folder, Footprint Fp, IReadOnlySet<string> Assets, HashSet<string> NonBaseMasters);

public static class SelectionRule
{
    public const int Ring = 1;
    public static readonly string[] ContainerKinds = { "WRLD record", "a worldspace's persistent CELL record", "NAVI" };
    public static readonly string[] SelectingKinds = { "records", "cells", "neighbours", "assets", "baseLinks" };
    public static readonly string[] NotedKinds = { "containerRecords", "ring2Cells", "sharedMasters", "formLinks", "reverseFormLinks" };
    public const string Ranking = "distinct selecting kinds met (more first), then shared cells, then shared records, then shared assets, then base links, then plugin name";

    const int Ex = 20;

    /// <summary>The pairwise rule: N against one installed plugin B. Returns the evidence, what was noted, and the
    /// selecting kinds met (empty: B is left out).</summary>
    public static (SelEvidence Ev, SelNoted Noted, List<string> Kinds) Judge(NewcomerFacts n, InstalledFacts b)
    {
        // RED STUB: the rule is not written yet; every installed plugin is left out with nothing noted.
        var none = Array.Empty<string>();
        return (new SelEvidence(0, none, 0, none, 0, none, 0, none, 0, 0, none), new SelNoted(0, 0, none, 0, null), new List<string>());
    }

    public static string ExcludedReason(SelNoted noted)
    {
        var parts = new List<string>();
        if (noted.ContainerRecords > 0) parts.Add($"container records ({noted.ContainerRecords})");
        if (noted.Ring2Cells > 0) parts.Add($"ring-2 neighbour cells ({noted.Ring2Cells})");
        if (noted.SharedMasters.Count > 0) parts.Add($"master(s) {string.Join(", ", noted.SharedMasters)}");
        if (noted.FormLinks > 0) parts.Add($"form links from the newcomer ({noted.FormLinks})");
        if (noted.ReverseFormLinks is > 0) parts.Add($"form links to the newcomer ({noted.ReverseFormLinks})");
        return parts.Count == 0 ? "nothing in common" : "shares only " + string.Join("; ", parts);
    }

    /// <summary>Rank the candidates by <see cref="Ranking"/>; the rank never drops anything.</summary>
    public static List<SelCandidate> Rank(IEnumerable<(string Plugin, string? Folder, SelEvidence Ev, SelNoted Noted, List<string> Kinds)> picked)
        => picked.OrderByDescending(p => p.Kinds.Count)
                 .ThenByDescending(p => p.Ev.Cells).ThenByDescending(p => p.Ev.Records).ThenByDescending(p => p.Ev.Assets)
                 .ThenByDescending(p => p.Ev.NewcomerPlacesTheirBase + p.Ev.TheyPlaceNewcomerBase)
                 .ThenBy(p => p.Plugin, StringComparer.OrdinalIgnoreCase)
                 .Select((p, i) => new SelCandidate(p.Plugin, p.Folder, i + 1, p.Kinds, p.Ev, p.Noted)).ToList();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
