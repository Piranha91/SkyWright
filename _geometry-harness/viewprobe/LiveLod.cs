using SkyRender;
#if SKYRENDER_LIVE_LOD
using SkyRender.World;
#endif

namespace ViewProbe;

/// <summary>
/// Distant LOD on the live scene (LiveScene.cs): SkyRender's <c>LiveDistantLod</c> streams terrain, object, tree and water
/// LOD blocks around the camera on a worker thread, and the renderer hides LOD under the loaded cells at draw time, so
/// neither a camera move nor a change of the loaded cells rebuilds anything. Built through the live session's texture
/// manager and resolver. Compiled against SkyRender 4.1 (<c>SKYRENDER_LIVE_LOD</c>); on an older SkyRender,
/// <see cref="Create"/> refuses with the reason.
/// <para>The distances are the profile's [TerrainManager] settings (fBlockLevel0Distance, fBlockLevel1Distance,
/// fBlockMaximumDistance, fSplitDistanceMult, fTreeLoadDistance), each the game's default when unset.</para>
/// </summary>
internal sealed class LiveLod : IDisposable
{
#if SKYRENDER_LIVE_LOD
    readonly LiveDistantLod _lod;
    readonly GlRenderer _renderer;
    LiveLod(LiveDistantLod lod, GlRenderer renderer, string source) { _lod = lod; _renderer = renderer; Source = source; }
#endif

    /// <summary>The LOD's worldspace and distances, in words.</summary>
    internal string Source { get; } = "";

    /// <summary>The live LOD for the scene's worldspace, or null with the reason (an interior, no LOD for the worldspace, or a
    /// SkyRender without live LOD). Call on the GL thread.</summary>
    internal static LiveLod? Create(LiveSession session, out string why)
    {
#if SKYRENDER_LIVE_LOD
        string? world = DistantLodLayer.LodWorldspace(out var worldWhy);
        if (world is null) { why = "no distant LOD: " + worldWhy; return null; }
        var said = new List<string>();
        float F(string key, float game) { float v = ProfileIni.Float("TerrainManager", key, game, out var s); if (s is not null) said.Add(s); return v; }
        var d = new LodDistances();
        d = d with
        {
            Level0Distance = F("fBlockLevel0Distance", d.Level0Distance),
            Level1Distance = F("fBlockLevel1Distance", d.Level1Distance),
            MaximumDistance = F("fBlockMaximumDistance", d.MaximumDistance),
            SplitDistanceMult = F("fSplitDistanceMult", d.SplitDistanceMult),
            TreeLoadDistance = F("fTreeLoadDistance", d.TreeLoadDistance),
        };
        var textures = session.Textures;
        var lod = new LiveDistantLod(session.Renderer, new LiveDistantLodOptions
        {
            Worldspace = world,
            ResolveDiskPath = p => session.Assets.Cache.ResolveAssetPath(p),
            Reader = session.Assets.Resolver,
            LoadTexture = p => { int h = textures.LoadTexture(p); return h == textures.WhiteTexture ? null : h; },
            WhiteTexture = textures.WhiteTexture,
            LodWater = DistantLodLayer.LodWaterMaterial(withNoise: true),
        })
        {
            Distances = d,
            MaxDistance = DistantLodLayer.FarPlane,
        };
        if (lod.Problem is { } p) { lod.Dispose(); why = "no distant LOD: " + p; return null; }
        why = "";
        return new LiveLod(lod, session.Renderer,
            $"{world}{worldWhy}; distances {(said.Count > 0 ? string.Join(", ", said) : "the game's defaults (the profile's INI sets none)")}");
#else
        why = "distant LOD on a live scene needs SkyRender 4.1 (this build uses an older SkyRender)";
        return null;
#endif
    }

    /// <summary>Every frame, with the eye in Skyrim units: streams the blocks the eye wants. True while blocks are still
    /// coming or going, so the caller keeps drawing frames.</summary>
    internal bool Update(System.Numerics.Vector3 eye)
    {
#if SKYRENDER_LIVE_LOD
        _lod.Update(eye);
        return !_lod.IsSettled;
#else
        return false;
#endif
    }

    /// <summary>The cells whose full terrain is drawn: LOD is hidden under them. Set after their terrain is added.</summary>
    internal void SetLoaded(IEnumerable<(int X, int Y)> cells)
    {
#if SKYRENDER_LIVE_LOD
        _renderer.SetLodLoadedCells(cells);
#endif
    }

    /// <summary>Which kinds draw (terrain, objects, trees, water); hidden kinds stay resident.</summary>
    internal void Show(bool terrain, bool objects, bool trees, bool water)
    {
#if SKYRENDER_LIVE_LOD
        _renderer.LodVisibleKinds = (terrain ? LodKinds.Terrain : LodKinds.None) | (objects ? LodKinds.Objects : LodKinds.None)
                                  | (trees ? LodKinds.Trees : LodKinds.None) | (water ? LodKinds.Water : LodKinds.None);
#endif
    }

    /// <summary>Resident and pending blocks, for the state readout.</summary>
    internal (int Resident, int Pending) Blocks
    {
        get
        {
#if SKYRENDER_LIVE_LOD
            return (_lod.ResidentBlocks, _lod.PendingBlocks);
#else
            return (0, 0);
#endif
        }
    }

    public void Dispose()
    {
#if SKYRENDER_LIVE_LOD
        _lod.Dispose();
#endif
    }
}
