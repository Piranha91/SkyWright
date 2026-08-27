using System.Diagnostics;
using System.Numerics;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
// WPF is pulled in by the CharacterViewer.Rendering reference and brings its own Path/Image/Color
// into scope; alias the ones this file means so the collisions cannot silently pick the wrong type.
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using Image = SixLabors.ImageSharp.Image;

// viewprobe — render a cell's WINNING geometry with its WINNING navmesh laid over it.
//
// Why this exists. Two sessions of milestone 4 produced percentages nobody could check by eye, and
// two open questions that numbers could not close: whether ToWorld composes Euler angles in the
// order the engine does, and whether a flagged reference is really blocking anything. Both are
// visual questions. This makes them visible.
//
// It is deliberately NOT a new renderer. The vertex layout below is byte-for-byte the one
// CharacterViewer.Rendering's GlMesh.Upload expects — position(3) normal(3) uv(2) colour(4)
// tangent(3) bitangent(3), 18 floats interleaved — so moving to that library's GlRenderer and
// NifMeshBuilder, and getting textures and its lighting rig with them, is a drop-in swap of the
// draw call rather than a reshaping of the data. This pass draws flat-lit geometry because gross
// structure is all the Euler question needs, and it lands tonight.
//
// usage: viewprobe <CellEditorID|FormID> [--out DIR] [--size N] [--smoke]
//                  [--angles a,b,c] [--elev N] [--no-nav] [--ref FORMID]

const string Instance = @"C:\Games\Skyrim VR\PiranhaRim VR";
const string GamePath = @"C:\games\steam\steamapps\common\SkyrimVR";
string profileDir = Path.Combine(Instance, "profiles", "PiranhaRimVR");
string modsDir = Path.Combine(Instance, "mods");
string overwriteDir = Path.Combine(Instance, "overwrite");
string dataDir = Path.Combine(GamePath, "Data");

if (args.Length > 0 && args[0] == "--smoke") return Smoke();
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: viewprobe <CellEditorID|FormID> [--out DIR] [--size N] [--angles 0,90,180] [--elev 25] [--no-nav] [--ref FORMID]");
    return 1;
}

string target = args[0];
string outDir = Path.Combine(AppContext.BaseDirectory, "shots");
int size = 1280;
float elev = 28f;
bool drawNav = true;
string? highlight = null;
var angles = new List<float> { 35f, 125f };
// --euler renders the SAME cell under all six Euler compositions. The numeric tests could not
// separate them (0.62%-0.69%, then 1.84%-2.08%), which is itself a finding — but a wrong order
// scatters kit pieces by hundreds of units, and that is trivially visible even when it is
// statistically invisible. This is the test the numbers could not run.
bool euler = false;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (args[i] == "--size" && i + 1 < args.Length) size = int.Parse(args[++i]);
    else if (args[i] == "--elev" && i + 1 < args.Length) elev = float.Parse(args[++i]);
    else if (args[i] == "--no-nav") drawNav = false;
    else if (args[i] == "--ref" && i + 1 < args.Length) highlight = args[++i];
    else if (args[i] == "--euler") euler = true;
    else if (args[i] == "--angles" && i + 1 < args.Length)
        angles = args[++i].Split(',').Select(float.Parse).ToList();
}
Directory.CreateDirectory(outDir);

// ---------------------------------------------------------------------------------------
// 1. Resolve the cell's winning state
// ---------------------------------------------------------------------------------------
var sw = Stopwatch.StartNew();
var comp = Mo2LoadOrder.ReadComposition(profileDir);
var arch = ArchiveDiscovery.Discover(profileDir, modsDir, dataDir, overwriteDir, GamePath);
using var assets = AssetResolver.Build(overwriteDir, modsDir, dataDir, comp.EnabledMods, arch.Archives);
var order = Mo2LoadOrder.Build(profileDir, modsDir, dataDir, overwriteDir);

var winRef = new Dictionary<FormKey, PlacedRef>();
var winNav = new Dictionary<FormKey, NavmeshGeometry>();
var baseMesh = new Dictionary<FormKey, string>();
FormKey? cellKey = null;
string cellLabel = target;
bool interior = false;

foreach (var path in order.OrderedPaths)
{
    ISkyrimModGetter m;
    try { m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); } catch { continue; }
    using (m as IDisposable)
    {
        foreach (var rec in m.EnumerateMajorRecords())
            if (rec is IModeledGetter mo && mo.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath))
                baseMesh[((ISkyrimMajorRecordGetter)rec).FormKey] = f.GivenPath;

        foreach (var cp in PlacementExtract.FromMod(m))
        {
            bool match = cellKey is { } ck ? cp.Cell == ck
                : string.Equals(cp.CellEditorId, target, StringComparison.OrdinalIgnoreCase)
                  || cp.Cell.ToString().Equals(target, StringComparison.OrdinalIgnoreCase)
                  || cp.Cell.ID.ToString("X6").Equals(target.TrimStart('0'), StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            cellKey ??= cp.Cell;
            cellLabel = cp.CellEditorId ?? cp.Cell.ToString();
            interior = cp.Interior;
            foreach (var r in cp.Refs) winRef[r.FormKey] = r;
            foreach (var nav in cp.Navmeshes) winNav[nav.FormKey] = nav;
        }
    }
}

if (cellKey is null) { Console.Error.WriteLine($"cell '{target}' not found in the active load order."); return 1; }
Console.WriteLine($"cell {cellLabel} {(interior ? "(interior)" : "(exterior)")}  {cellKey}");
Console.WriteLine($"winning state: {winRef.Count:N0} refs, {winNav.Count:N0} navmeshes  ({sw.Elapsed.TotalSeconds:0.0}s)");

// ---------------------------------------------------------------------------------------
// 2. Build world geometry
// ---------------------------------------------------------------------------------------
var verts = new List<float>();      // 18 floats per vertex — GlMesh.Upload's exact layout
var idx = new List<int>();
var navVerts = new List<float>();
var navIdx = new List<int>();
var lineVerts = new List<float>();

var geoCache = new Dictionary<string, TriangleSoup?>(StringComparer.OrdinalIgnoreCase);
int drawn = 0, noGeo = 0;
long triCount = 0;
var bounds = Aabb.Empty;

FormKey? hi = null;
if (highlight is not null) { try { hi = FormKey.Factory(highlight); } catch { } }

var activeOrder = SpatialOverlap.EulerOrder.Xyz;

void Build(SpatialOverlap.EulerOrder order)
{
    activeOrder = order;
    verts.Clear(); idx.Clear(); navVerts.Clear(); navIdx.Clear(); lineVerts.Clear();
    drawn = 0; noGeo = 0; triCount = 0; bounds = Aabb.Empty;

    sw.Restart();
    foreach (var r in winRef.Values)
    {
        if (r.InitiallyDisabled || r.Deleted) continue;
        if (r.Base is not { } bk || !baseMesh.TryGetValue(bk, out var rel)) { noGeo++; continue; }

        if (!geoCache.TryGetValue(rel, out var soup))
        {
            soup = null;
            var full = rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("meshes", rel);
            var res = assets.ResolveForPlacement(full);
            if (res.Sources.Count > 0)
            {
                var (bytes, _) = AssetResolver.ReadPlacementSource(res.Sources[0]);
                if (bytes is not null)
                {
                    var o = NifGeometry.Measure(bytes, includeTriangles: true);
                    if (o.Model?.Soup is { TriangleCount: > 0 and <= 300_000 } s) soup = s;
                }
            }
            geoCache[rel] = soup;
        }
        if (soup is null) { noGeo++; continue; }

        // The SAME composition ToWorld uses, so what is drawn is what the detector measured. If the
        // order is wrong, this picture is wrong in exactly the way the numbers are — which is the point.
        var mat = SpatialOverlap.Euler(r.Rotation, activeOrder);
        var t = new Vector3(r.Position.X, r.Position.Y, r.Position.Z);

        bool isHi = hi is { } h && r.FormKey == h;
        // A stable per-object tint so separate objects read as separate objects; the highlight overrides.
        uint seed = (uint)(r.FormKey.ID * 2654435761u);
        var col = isHi ? new Vector4(1.0f, 0.35f, 0.1f, 1f)
                       : new Vector4(0.55f + ((seed >> 8) & 63) / 255f,
                                     0.52f + ((seed >> 16) & 63) / 255f,
                                     0.48f + ((seed >> 24) & 63) / 255f, 1f);

        int baseIdx = verts.Count / 18;
        var world = new Vector3[soup.Vertices.Length];
        for (int i = 0; i < world.Length; i++)
        {
            world[i] = Vector3.Transform(soup.Vertices[i] * r.Scale, mat) + t;
            bounds = bounds.Add(world[i]);
        }
        // Flat shading: a face normal per triangle needs its own vertices, which also keeps the kit
        // pieces legible instead of smoothing every edge away.
        for (int i = 0; i + 2 < soup.Indices.Length; i += 3)
        {
            var a = world[soup.Indices[i]]; var b = world[soup.Indices[i + 1]]; var c = world[soup.Indices[i + 2]];
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
            int at = verts.Count / 18;
            Push(verts, a, n, col); Push(verts, b, n, col); Push(verts, c, n, col);
            idx.Add(at); idx.Add(at + 1); idx.Add(at + 2);
            triCount++;
        }
        drawn++;
    }

    if (drawNav)
    {
        var navCol = new Vector4(0.20f, 0.95f, 0.45f, 0.42f);
        foreach (var nav in winNav.Values)
            foreach (var tri in nav.Triangles)
            {
                if ((tri.Flags & 0x08) != 0) continue;
                if (tri.A < 0 || tri.B < 0 || tri.C < 0) continue;
                if (tri.A >= nav.Vertices.Count || tri.B >= nav.Vertices.Count || tri.C >= nav.Vertices.Count) continue;
                var a = V(nav.Vertices[tri.A]); var b = V(nav.Vertices[tri.B]); var c = V(nav.Vertices[tri.C]);
                var n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
                int at = navVerts.Count / 18;
                Push(navVerts, a, n, navCol); Push(navVerts, b, n, navCol); Push(navVerts, c, n, navCol);
                navIdx.Add(at); navIdx.Add(at + 1); navIdx.Add(at + 2);
                bounds = bounds.Add(a); bounds = bounds.Add(b); bounds = bounds.Add(c);

                var edge = new Vector4(0.05f, 0.55f, 0.25f, 1f);
                var lift = new Vector3(0, 0, 1.5f);   // hairline lift so edges are not z-fought away
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                { Push(lineVerts, p + lift, n, edge); Push(lineVerts, q + lift, n, edge); }
            }
    }
}

Build(activeOrder);

Console.WriteLine($"geometry: {drawn:N0} refs drawn ({noGeo:N0} without usable mesh), {triCount:N0} triangles, "
                + $"{navIdx.Count / 3:N0} navmesh triangles  ({sw.Elapsed.TotalSeconds:0.0}s)");
if (bounds.IsEmpty) { Console.Error.WriteLine("nothing to draw."); return 1; }
Console.WriteLine($"bounds  : {bounds}");

// ---------------------------------------------------------------------------------------
// 3. Render
// ---------------------------------------------------------------------------------------
var nws = new NativeWindowSettings
{
    ClientSize = new OpenTK.Mathematics.Vector2i(size, size),
    StartVisible = false,
    StartFocused = false,
    WindowBorder = WindowBorder.Hidden,
    APIVersion = new Version(3, 3),
    Profile = ContextProfile.Core,
};
using var gw = new GameWindow(GameWindowSettings.Default, nws);
gw.MakeCurrent();

int prog = Shader();
int nOpaque;
int vaoOpaque = Upload(verts, idx, out nOpaque);
int vaoNav = navIdx.Count > 0 ? Upload(navVerts, navIdx, out _) : 0;
int nNavIdx = navIdx.Count;
int vaoLines = lineVerts.Count > 0 ? UploadLines(lineVerts, out _) : 0;
int nLineVerts = lineVerts.Count / 18;

int fbo = GL.GenFramebuffer(), colorTex = GL.GenTexture(), depthRb = GL.GenRenderbuffer();
GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
GL.BindTexture(TextureTarget.Texture2D, colorTex);
GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);
GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depthRb);
if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
{ Console.Error.WriteLine("framebuffer incomplete."); return 1; }

var c0 = bounds.Center;
var centre = new Vector3(c0.X, c0.Y, c0.Z);
var sz = bounds.Size;
float span = MathF.Max(sz.X, MathF.Max(sz.Y, sz.Z));

var shots = new List<string>();
if (euler)
{
    // One angle, six compositions. A wrong order does not shift a building subtly — it takes every
    // multi-axis reference and throws it hundreds of units, which the eye separates instantly and
    // the containment statistic could not separate at all.
    foreach (var ord in Enum.GetValues<SpatialOverlap.EulerOrder>())
    {
        Build(ord);
        GL.DeleteVertexArray(vaoOpaque);
        vaoOpaque = Upload(verts, idx, out nOpaque);
        if (vaoNav != 0) { GL.DeleteVertexArray(vaoNav); vaoNav = Upload(navVerts, navIdx, out _); nNavIdx = navIdx.Count; }
        if (vaoLines != 0) { GL.DeleteVertexArray(vaoLines); vaoLines = UploadLines(lineVerts, out _); nLineVerts = lineVerts.Count / 18; }
        var bc = bounds.Center; centre = new Vector3(bc.X, bc.Y, bc.Z);
        var bs = bounds.Size; span = MathF.Max(bs.X, MathF.Max(bs.Y, bs.Z));
        shots.Add(RenderOne(angles[0], elev, $"{Safe(cellLabel)}-euler-{ord.ToString().ToUpperInvariant()}.png"));
        Console.WriteLine($"   {ord,-4} bounds {bounds}");
    }
    GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);
    Console.WriteLine();
    Console.WriteLine("wrote:");
    foreach (var sh in shots) Console.WriteLine($"   {sh}");
    return 0;
}

foreach (var az in angles) shots.Add(RenderOne(az, elev, $"{Safe(cellLabel)}-az{az:0}.png"));
shots.Add(RenderOne(0, 89.5f, $"{Safe(cellLabel)}-plan.png"));      // the view that reads unambiguously

GL.DeleteFramebuffer(fbo); GL.DeleteTexture(colorTex); GL.DeleteRenderbuffer(depthRb);
Console.WriteLine("\nwrote:");
foreach (var s in shots) Console.WriteLine($"   {s}");
return 0;

// ---------------------------------------------------------------------------------------
string RenderOne(float azDeg, float elDeg, string file)
{
    float az = azDeg * MathF.PI / 180f, el = elDeg * MathF.PI / 180f;
    float dist = span * 1.15f + 200f;
    var eye = centre + new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el)) * dist;
    var up = elDeg > 88f ? Vector3.UnitY : Vector3.UnitZ;

    var view = Matrix4x4.CreateLookAt(eye, centre, up);
    var proj = Matrix4x4.CreatePerspectiveFieldOfView(50f * MathF.PI / 180f, 1f, MathF.Max(1f, dist - span * 2f), dist + span * 3f);
    var mvp = view * proj;

    GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
    GL.Viewport(0, 0, size, size);
    GL.Enable(EnableCap.DepthTest);
    GL.ClearColor(0.10f, 0.11f, 0.13f, 1f);
    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

    GL.UseProgram(prog);
    SetMat(prog, "uMvp", mvp);
    var lightDir = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    GL.Uniform3(GL.GetUniformLocation(prog, "uLight"), lightDir.X, lightDir.Y, lightDir.Z);

    // Opaque first, then navmesh blended with depth test but no depth write, so navmesh that is
    // genuinely buried inside geometry is HIDDEN by it — which is the whole thing being looked for.
    GL.Disable(EnableCap.Blend);
    GL.DepthMask(true);
    GL.BindVertexArray(vaoOpaque);
    GL.DrawElements(PrimitiveType.Triangles, nOpaque, DrawElementsType.UnsignedInt, 0);

    if (vaoNav != 0)
    {
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        GL.BindVertexArray(vaoNav);
        GL.DrawElements(PrimitiveType.Triangles, nNavIdx, DrawElementsType.UnsignedInt, 0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }
    if (vaoLines != 0)
    {
        GL.BindVertexArray(vaoLines);
        GL.DrawArrays(PrimitiveType.Lines, 0, nLineVerts);
    }

    var px = new byte[size * size * 4];
    GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, px);

    using var img = Image.LoadPixelData<Rgba32>(px, size, size);
    img.Mutate(x => x.Flip(SixLabors.ImageSharp.Processing.FlipMode.Vertical));   // GL origin is bottom-left
    string outPath = Path.Combine(outDir, file);
    img.SaveAsPng(outPath);
    return outPath;
}

static void Push(List<float> dst, Vector3 p, Vector3 n, Vector4 c)
{
    dst.Add(p.X); dst.Add(p.Y); dst.Add(p.Z);
    dst.Add(n.X); dst.Add(n.Y); dst.Add(n.Z);
    dst.Add(0); dst.Add(0);                                   // uv — unused until textures land
    dst.Add(c.X); dst.Add(c.Y); dst.Add(c.Z); dst.Add(c.W);
    dst.Add(0); dst.Add(0); dst.Add(0);                       // tangent
    dst.Add(0); dst.Add(0); dst.Add(0);                       // bitangent
}

static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);
static string Safe(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));

static int Upload(List<float> v, List<int> i, out int count)
{
    count = i.Count;
    int vao = GL.GenVertexArray(), vbo = GL.GenBuffer(), ebo = GL.GenBuffer();
    GL.BindVertexArray(vao);
    var va = v.ToArray(); var ia = i.ToArray();
    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
    GL.BufferData(BufferTarget.ArrayBuffer, va.Length * sizeof(float), va, BufferUsageHint.StaticDraw);
    GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
    GL.BufferData(BufferTarget.ElementArrayBuffer, ia.Length * sizeof(int), ia, BufferUsageHint.StaticDraw);
    Attribs();
    GL.BindVertexArray(0);
    return vao;
}

static int UploadLines(List<float> v, out int count)
{
    count = v.Count / 18;
    int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
    GL.BindVertexArray(vao);
    var va = v.ToArray();
    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
    GL.BufferData(BufferTarget.ArrayBuffer, va.Length * sizeof(float), va, BufferUsageHint.StaticDraw);
    Attribs();
    GL.BindVertexArray(0);
    return vao;
}

// The 18-float interleave CharacterViewer.Rendering's GlMesh uses, attribute for attribute.
static void Attribs()
{
    int stride = 18 * sizeof(float), off = 0;
    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(0); off += 3 * sizeof(float);
    GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(1); off += 3 * sizeof(float);
    GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(2); off += 2 * sizeof(float);
    GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(3); off += 4 * sizeof(float);
    GL.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(4); off += 3 * sizeof(float);
    GL.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, stride, off); GL.EnableVertexAttribArray(5);
}

static void SetMat(int prog, string name, Matrix4x4 m)
{
    Span<float> f = stackalloc float[16]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };
    GL.UniformMatrix4(GL.GetUniformLocation(prog, name), 1, false, ref f[0]);
}

static int Shader()
{
    const string vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNrm;
layout(location=2) in vec2 aUv;
layout(location=3) in vec4 aCol;
uniform mat4 uMvp;
out vec3 vN; out vec4 vC;
void main(){ vN = aNrm; vC = aCol; gl_Position = uMvp * vec4(aPos,1.0); }";

    const string fs = @"#version 330 core
in vec3 vN; in vec4 vC;
uniform vec3 uLight;
out vec4 frag;
void main(){
    vec3 n = normalize(vN);
    // Two-sided: Skyrim kit meshes are full of inward-facing shells and a one-sided lambert
    // renders half of every room pitch black.
    float d = abs(dot(n, uLight));
    float sky = 0.35 + 0.35 * clamp(n.z * 0.5 + 0.5, 0.0, 1.0);
    frag = vec4(vC.rgb * (sky + 0.65 * d), vC.a);
}";
    int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v); Check(v, "vertex");
    int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f); Check(f, "fragment");
    int p = GL.CreateProgram(); GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
    GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
    if (ok == 0) throw new InvalidOperationException("link failed: " + GL.GetProgramInfoLog(p));
    GL.DeleteShader(v); GL.DeleteShader(f);
    return p;

    static void Check(int s, string what)
    {
        GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{what} shader: {GL.GetShaderInfoLog(s)}");
    }
}

static int Smoke()
{
    var nws = new NativeWindowSettings
    {
        ClientSize = new OpenTK.Mathematics.Vector2i(64, 64),
        StartVisible = false,
        APIVersion = new Version(3, 3),
        Profile = ContextProfile.Core,
    };
    using var gw = new GameWindow(GameWindowSettings.Default, nws);
    gw.MakeCurrent();
    Console.WriteLine($"GL {GL.GetString(StringName.Version)} on {GL.GetString(StringName.Renderer)}");
    return 0;
}
