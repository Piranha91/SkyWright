using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using V3 = System.Numerics.Vector3;

namespace ViewProbe;

/// <summary>
/// One prepared area of a live scene (LiveScene.cs), written by a viewprobe run in its own process (<c>--prepare-out</c>)
/// and read by the open scene, which adds the cells it does not hold yet. The walk and its preparation keep state no
/// running scene can share, so a second area is prepared in a process of its own and handed over as this file: the
/// references, terrain sheets and layers, water planes and land heights of each cell, the land layer census the grass
/// ground is built from (by the open scene's own records index), and the walk's facts about the area's references.
/// <para>A binary file with a version: a reader refuses one it does not know, in one sentence.</para>
/// </summary>
internal sealed class LiveArea
{
    const int Version = 1;
    const string Magic = "SWLIVEAREA";

    internal (int X, int Y) Centre;
    internal FormKey Worldspace = FormKey.Null;
    internal HashSet<(int X, int Y)> Cells = new();
    internal List<SceneRef> Refs = new();
    internal Dictionary<string, List<(V3, V3, V3)>> Patches = new(StringComparer.OrdinalIgnoreCase);
    internal List<(string Diffuse, List<(V3 A, V3 B, V3 C, float Oa, float Ob, float Oc)> Tris)> Layers = new();
    internal List<WaterPlane> Water = new();
    internal Dictionary<(int X, int Y), float[,]> LandHeights = new();
    internal Dictionary<(int X, int Y), V3[]> Normals = new(), Colors = new();
    internal Dictionary<(int X, int Y), List<(int Quadrant, int Layer, bool Alpha, FormKey? Tex, int Painted, float MaxOpacity, float[]? Opacity)>> Census = new();
    internal Dictionary<(int X, int Y), float> WaterHeights = new();
    internal HashSet<FormKey> Large = new();
    internal Dictionary<FormKey, FormKey> RefCells = new();
    internal Dictionary<FormKey, (int X, int Y)> CellGrids = new();
    internal Dictionary<FormKey, (string Outcome, string? BaseEditorId)> Outcomes = new();
    internal Dictionary<FormKey, string> BaseTypes = new();
    internal Dictionary<FormKey, (Vec3 Position, Vec3 Rotation)> Undrawn = new();

    // ---- writing -------------------------------------------------------------------------------------------------------
    internal void Write(string path)
    {
        var tmp = path + ".tmp";
        using (var f = new BinaryWriter(File.Create(tmp)))
        {
            f.Write(Magic); f.Write(Version);
            f.Write(Centre.X); f.Write(Centre.Y); K(f, Worldspace);
            f.Write(Cells.Count); foreach (var c in Cells) G(f, c);
            f.Write(Refs.Count);
            foreach (var r in Refs)
            {
                f.Write(r.MeshRelPath); P(f, r.Position); P(f, r.Rotation); f.Write(r.Scale); f.Write(r.Highlight); f.Write(r.Marker);
                K(f, r.RefKey); K(f, r.BaseKey); S(f, r.BaseEditorId); f.Write(r.BosRecordBase is { } bb); if (r.BosRecordBase is { } b2) K(f, b2);
                S(f, r.BosLabel); f.Write(r.BosRandom); f.Write(r.StateChange); S(f, r.StateLabel); f.Write(r.Constructed); V(f, r.HighlightColor);
            }
            f.Write(Patches.Count);
            foreach (var (tex, tris) in Patches) { f.Write(tex); f.Write(tris.Count); foreach (var (a, b, c) in tris) { V(f, a); V(f, b); V(f, c); } }
            f.Write(Layers.Count);
            foreach (var (tex, tris) in Layers)
            {
                f.Write(tex); f.Write(tris.Count);
                foreach (var t in tris) { V(f, t.A); V(f, t.B); V(f, t.C); f.Write(t.Oa); f.Write(t.Ob); f.Write(t.Oc); }
            }
            f.Write(Water.Count);
            foreach (var w in Water)
            {
                f.Write(w.Z); f.Write(w.X0); f.Write(w.Y0); f.Write(w.X1); f.Write(w.Y1); S(f, w.NoiseTexture); f.Write(w.NoiseUvScale);
                V(f, w.ShallowColor); V(f, w.DeepColor); V(f, w.ReflectionColor); f.Write(w.Reflectivity); f.Write(w.Opacity); f.Write(w.Fresnel);
                f.Write(w.Label); f.Write(w.FogNear); f.Write(w.FogFar); f.Write(w.FogAmount); f.Write(w.NoiseFalloff); V(f, w.Spec);
            }
            f.Write(LandHeights.Count);
            foreach (var (g, h) in LandHeights)
            {
                G(f, g); f.Write(h.GetLength(0)); f.Write(h.GetLength(1));
                for (int x = 0; x < h.GetLength(0); x++) for (int y = 0; y < h.GetLength(1); y++) f.Write(h[x, y]);
            }
            foreach (var d in new[] { Normals, Colors }) { f.Write(d.Count); foreach (var (g, a) in d) { G(f, g); f.Write(a.Length); foreach (var v in a) V(f, v); } }
            f.Write(Census.Count);
            foreach (var (g, list) in Census)
            {
                G(f, g); f.Write(list.Count);
                foreach (var l in list)
                {
                    f.Write(l.Quadrant); f.Write(l.Layer); f.Write(l.Alpha); f.Write(l.Tex is { } t); if (l.Tex is { } t2) K(f, t2);
                    f.Write(l.Painted); f.Write(l.MaxOpacity); f.Write(l.Opacity is not null);
                    if (l.Opacity is { } o) { f.Write(o.Length); foreach (var v in o) f.Write(v); }
                }
            }
            f.Write(WaterHeights.Count); foreach (var (g, h) in WaterHeights) { G(f, g); f.Write(h); }
            f.Write(Large.Count); foreach (var k in Large) K(f, k);
            f.Write(RefCells.Count); foreach (var (a, b) in RefCells) { K(f, a); K(f, b); }
            f.Write(CellGrids.Count); foreach (var (k, g) in CellGrids) { K(f, k); G(f, g); }
            f.Write(Outcomes.Count); foreach (var (k, o) in Outcomes) { K(f, k); f.Write(o.Outcome); S(f, o.BaseEditorId); }
            f.Write(BaseTypes.Count); foreach (var (k, t) in BaseTypes) { K(f, k); f.Write(t); }
            f.Write(Undrawn.Count); foreach (var (k, u) in Undrawn) { K(f, k); P(f, u.Position); P(f, u.Rotation); }
        }
        File.Move(tmp, path, overwrite: true);   // a reader never meets a half-written file
    }

    static void K(BinaryWriter f, FormKey k) => f.Write(k.IsNull ? "" : k.ToString());
    static void G(BinaryWriter f, (int X, int Y) g) { f.Write(g.X); f.Write(g.Y); }
    static void P(BinaryWriter f, Vec3 v) { f.Write(v.X); f.Write(v.Y); f.Write(v.Z); }
    static void V(BinaryWriter f, V3 v) { f.Write(v.X); f.Write(v.Y); f.Write(v.Z); }
    static void S(BinaryWriter f, string? s) { f.Write(s is not null); if (s is not null) f.Write(s); }

    // ---- reading -------------------------------------------------------------------------------------------------------
    /// <summary>The area in <paramref name="path"/>; refuses a file of another kind or version in one sentence.</summary>
    internal static LiveArea Read(string path)
    {
        try { return ReadAll(path); }
        catch (EndOfStreamException) { throw new InvalidDataException($"{path} is not a whole prepared live-scene area (it ends early): prepare it again."); }
    }

    static LiveArea ReadAll(string path)
    {
        using var f = new BinaryReader(File.OpenRead(path));
        if (f.ReadString() != Magic) throw new InvalidDataException($"{path} is not a prepared live-scene area.");
        int v = f.ReadInt32();
        if (v != Version) throw new InvalidDataException($"{path} is a prepared area of format {v}; this build reads format {Version}: prepare it again.");
        var a = new LiveArea { Centre = (f.ReadInt32(), f.ReadInt32()), Worldspace = K(f) };
        for (int n = f.ReadInt32(); n > 0; n--) a.Cells.Add(G(f));
        for (int n = f.ReadInt32(); n > 0; n--)
        {
            string mesh = f.ReadString(); var pos = P(f); var rot = P(f); float scale = f.ReadSingle(); bool hl = f.ReadBoolean(), marker = f.ReadBoolean();
            var refKey = K(f); var baseKey = K(f); var baseEd = S(f); FormKey? bos = f.ReadBoolean() ? K(f) : null;
            var bosLabel = S(f); bool bosRandom = f.ReadBoolean(); int state = f.ReadInt32(); var stateLabel = S(f); bool constructed = f.ReadBoolean(); var hc = V(f);
            a.Refs.Add(new SceneRef(mesh, pos, rot, scale, hl, marker, refKey, baseKey, baseEd, bos, bosLabel, bosRandom, state, stateLabel, constructed, hc));
        }
        for (int n = f.ReadInt32(); n > 0; n--)
        {
            string tex = f.ReadString(); var tris = new List<(V3, V3, V3)>();
            for (int m = f.ReadInt32(); m > 0; m--) tris.Add((V(f), V(f), V(f)));
            a.Patches[tex] = tris;
        }
        for (int n = f.ReadInt32(); n > 0; n--)
        {
            string tex = f.ReadString(); var tris = new List<(V3 A, V3 B, V3 C, float Oa, float Ob, float Oc)>();
            for (int m = f.ReadInt32(); m > 0; m--) tris.Add((V(f), V(f), V(f), f.ReadSingle(), f.ReadSingle(), f.ReadSingle()));
            a.Layers.Add((tex, tris));
        }
        for (int n = f.ReadInt32(); n > 0; n--)
            a.Water.Add(new WaterPlane(f.ReadSingle(), f.ReadSingle(), f.ReadSingle(), f.ReadSingle(), f.ReadSingle(), S(f), f.ReadSingle(),
                V(f), V(f), V(f), f.ReadSingle(), f.ReadSingle(), f.ReadSingle(), f.ReadString(), f.ReadSingle(), f.ReadSingle(), f.ReadSingle(), f.ReadSingle(), V(f)));
        for (int n = f.ReadInt32(); n > 0; n--)
        {
            var g = G(f); int w = f.ReadInt32(), h = f.ReadInt32(); var hs = new float[w, h];
            for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) hs[x, y] = f.ReadSingle();
            a.LandHeights[g] = hs;
        }
        foreach (var d in new[] { a.Normals, a.Colors })
            for (int n = f.ReadInt32(); n > 0; n--) { var g = G(f); var arr = new V3[f.ReadInt32()]; for (int i = 0; i < arr.Length; i++) arr[i] = V(f); d[g] = arr; }
        for (int n = f.ReadInt32(); n > 0; n--)
        {
            var g = G(f); var list = new List<(int, int, bool, FormKey?, int, float, float[]?)>();
            for (int m = f.ReadInt32(); m > 0; m--)
            {
                int q = f.ReadInt32(), layer = f.ReadInt32(); bool alpha = f.ReadBoolean(); FormKey? tex = f.ReadBoolean() ? K(f) : null;
                int painted = f.ReadInt32(); float max = f.ReadSingle(); float[]? op = null;
                if (f.ReadBoolean()) { op = new float[f.ReadInt32()]; for (int i = 0; i < op.Length; i++) op[i] = f.ReadSingle(); }
                list.Add((q, layer, alpha, tex, painted, max, op));
            }
            a.Census[g] = list;
        }
        for (int n = f.ReadInt32(); n > 0; n--) a.WaterHeights[G(f)] = f.ReadSingle();
        for (int n = f.ReadInt32(); n > 0; n--) a.Large.Add(K(f));
        for (int n = f.ReadInt32(); n > 0; n--) a.RefCells[K(f)] = K(f);
        for (int n = f.ReadInt32(); n > 0; n--) a.CellGrids[K(f)] = G(f);
        for (int n = f.ReadInt32(); n > 0; n--) a.Outcomes[K(f)] = (f.ReadString(), S(f));
        for (int n = f.ReadInt32(); n > 0; n--) a.BaseTypes[K(f)] = f.ReadString();
        for (int n = f.ReadInt32(); n > 0; n--) a.Undrawn[K(f)] = (P(f), P(f));
        return a;
    }

    static FormKey K(BinaryReader f) { var s = f.ReadString(); return s.Length == 0 ? FormKey.Null : FormKey.Factory(s); }
    static (int X, int Y) G(BinaryReader f) => (f.ReadInt32(), f.ReadInt32());
    static Vec3 P(BinaryReader f) => new(f.ReadSingle(), f.ReadSingle(), f.ReadSingle());
    static V3 V(BinaryReader f) => new(f.ReadSingle(), f.ReadSingle(), f.ReadSingle());
    static string? S(BinaryReader f) => f.ReadBoolean() ? f.ReadString() : null;
}
