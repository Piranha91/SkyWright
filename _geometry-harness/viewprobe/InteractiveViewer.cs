using System.Diagnostics;
using System.Numerics;
using CharacterViewer.Rendering;
using HarnessShared;
using Mutagen.Bethesda.Plugins;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using GlMatrix4 = OpenTK.Mathematics.Matrix4;
using Path = System.IO.Path;
using Image = SixLabors.ImageSharp.Image;

namespace ViewProbe;

// INTERACTIVE FLY-THROUGH — the same scene RenderToPng builds, kept resident and drawn per
// frame into a visible window instead of once into a PNG.
//
// WHAT STAYS FROZEN AT THE ENTRY CAMERA, AND WHY THAT IS SOUND. The overlay's camera-dependent
// work — wire dash sizing, the visible/hidden classification, the adaptive nav colour, the cell
// outline's camera-facing ribbons — is built ONCE, for the camera the run started with. The
// "depth buffer is the arbiter" design is what makes that correct rather than merely cheap:
// every wire segment has a solid depth-tested copy (clipped per pixel, per frame, by the REAL
// depth buffer) and an always-visible dashed copy, so flying somewhere new never makes navmesh
// disappear — at most a dash is sized for where the camera used to be. Cosmetic, not semantic.
//
// PICKING is a CPU raycast against the same world-baked triangles the GPU draws (per-shape AABB
// shortlist, then Möller–Trumbore), NOT a depth-buffer unproject — so unlike --pick it can name
// alpha-blended geometry, and it knows WHICH shape it hit, not just where. It is geometric:
// a hit on the transparent part of a leaf texture still reports the leaf's ref. NPC actors are
// not pickable (their CPU-side geometry lives in the character pipeline's VMs); they already
// carry captions and pucks.
//
// WHAT THE LOOP CHANGES, AND WHAT IT CANNOT. Three things are live: the navmesh DISPLAY mode
// (N), a height band that hides whole static shapes (the clip keys), and the flight feel
// (speed, look sensitivity). All three are visibility or input state — none of them rebuilds
// geometry, so none can change what a static re-render of the same pose produces. The clip is
// therefore SHAPE-granular, not triangle-granular: a shape is hidden only when it lies wholly
// outside the band, so a wall spanning the cut keeps standing whole. --cull-above remains the
// triangle-exact knife, and it runs at build time; the clip keys print their band as a
// --cull-above line so a slice found by flying can be cut properly in the deliverable.

enum PickKind { Ref, Navmesh, Terrain }

/// <summary>One hover/click-able shape: world-baked Y-up triangles plus the identity to report.
/// Verts/Tris are the exact arrays the GlMesh upload used (shared, not copied), so picking
/// tests what is drawn — post-cull, post-filter.</summary>
sealed class PickShape
{
    public PickKind Kind;
    public Vector3[] Verts = Array.Empty<Vector3>();   // Y-up viewer space
    public int[] Tris = Array.Empty<int>();            // 3 indices per triangle
    public int[]? NavTriIds;                           // Kind==Navmesh: local tri -> record tri index
    public SceneRef Ref;                               // Kind==Ref
    public FormKey NavKey;                             // Kind==Navmesh
    public string TerrainTex = "";                     // Kind==Terrain ("" = untextured fallback)
    public Vector3 Min, Max;
    /// <summary>Kind==Ref: the GlMesh these triangles were uploaded as, so the height clip can
    /// hide it and the ray can then SKIP it. A pick that names a shape the clip just removed
    /// would have the HUD contradicting the picture. Null for navmesh and terrain, which the
    /// clip never touches.</summary>
    public GlMesh? Mesh;

    public void ComputeBounds()
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var i in Tris) { mn = Vector3.Min(mn, Verts[i]); mx = Vector3.Max(mx, Verts[i]); }
        Min = mn; Max = mx;
    }
}

/// <summary>Everything the fly-through needs that the scene build cannot derive itself:
/// the window, identity formatters, and the annotation-layer identities. PickShapes and
/// NavFill/NavFillRed are OUTPUTS of the build — TexturedScene fills them while baking the
/// scene, and the loop drives them live.</summary>
sealed class InteractiveOptions
{
    public required GameWindow Window;
    public required Func<FormKey, string> RuntimeId;
    public Func<float, float, string?>? TerrainDescribe;   // Skyrim X,Y -> quadrant/layer description
    public required string CellLabel;
    public required string OutDir;
    public required string SafeName;
    public HashSet<FormKey> RedNav = new();                // conflict-red navmeshes, tagged in reports
    public List<(FormKey Key, int Tri, Vector3 A, Vector3 B, Vector3 C)> NavTris = new();  // Skyrim space
    public List<PickShape> PickShapes = new();             // filled by the scene build

    // ---- navmesh display, switched live by N --------------------------------------------
    // Under --interactive the build makes BOTH the sheet and the wire whatever --nav asked
    // for (see TexturedScene), so switching between them costs a visibility flip, not a
    // rebuild. NavMode is the ENTRY mode — what the window opens showing — and stays "off"
    // when the run carries no navmesh at all, which is what tells N there is nothing to cycle.
    public string NavMode = "off";                         // off | filled | wire | both
    public List<GlMesh> NavFill = new();                   // healthy translucent sheet
    public List<GlMesh> NavFillRed = new();                // the finding's sheet
    public bool ShowNavWire = true;                        // read per frame by the overlay draw
    public bool ShowNavRedWire = true;

    // ---- the route, switched live by R ---------------------------------------------------
    // Route geometry is overlay geometry, so the fly-through gets it for nothing — which is what
    // makes this a LIVE PREVIEW of the path rather than a picture of one. R hides it, because the
    // one thing a person wants to check about a drawn route is what is underneath it.
    public int RouteCount;                                 // waypoints; 0 = this run carries no route
    public string RouteLabel = "";                         // the sentence that says which path this is
    public bool ShowRoute = true;                          // read per frame by the overlay draw

    // The height clip needs no list of its own: a static's PickShape already carries its mesh
    // and, in Min.Y/Max.Y, the Skyrim-z span of exactly the triangles that were drawn. Statics
    // only, like --cull-above — terrain, navmesh, actors and the overlay are never clipped,
    // because the subject of the picture must not be cullable by the knife that exists to
    // reveal it.

    // ---- flight feel, seeded from the command line ----------------------------------------
    public float FlySpeed = 600f;                          // u/s, before the shift/ctrl multiplier
    public float LookSens = 0.15f;                         // degrees of turn per pixel of mouse

    // ---- worklist: the shot list this window is stepping through ---------------------------
    // PageUp/PageDown move between the SHOTS of the cell this scene holds — a camera jump, free,
    // because everything is already resident. Ctrl+PageUp/PageDown move between CELLS, which the
    // loop cannot do itself: a different cell is a different scene. It asks instead, by setting
    // CellSwitch and closing the window; the host rebuilds and reopens at that shot.
    public WorklistFile? Worklist;
    public int WorklistCell;                               // index into Worklist.Cells that THIS scene is
    public int WorklistShot;                               // shot to open on
    public bool CanSwitchCell;                             // false when the host cannot rebuild (says so on the key)
    public (int Cell, int Shot)? CellSwitch;               // set by Ctrl+PageUp/Down, read by the host after Run returns
}

static class InteractiveViewer
{
    // The textured path's frustum, exactly.
    const float NearPlane = 4f, FarPlane = 131072f;

    internal static void Run(
        InteractiveOptions opt,
        GlRenderer renderer,
        (Vector3 Eye, Vector3 Look, float FovDeg) cam,
        Action<GlMatrix4, GlMatrix4>? overlayDraw)
    {
        var gw = opt.Window;

        // Navmesh pick shapes: the annotated triangle list, grouped per record, converted to
        // the same Y-up space the ref shapes already live in.
        foreach (var g in opt.NavTris.GroupBy(t => t.Key))
        {
            var verts = new List<Vector3>(); var tris = new List<int>(); var ids = new List<int>();
            foreach (var (_, ti, a, b, c) in g)
            {
                int at = verts.Count;
                verts.Add(TexturedScene.YUp(a)); verts.Add(TexturedScene.YUp(b)); verts.Add(TexturedScene.YUp(c));
                tris.Add(at); tris.Add(at + 1); tris.Add(at + 2);
                ids.Add(ti);
            }
            var ps = new PickShape { Kind = PickKind.Navmesh, NavKey = g.Key, Verts = verts.ToArray(), Tris = tris.ToArray(), NavTriIds = ids.ToArray() };
            ps.ComputeBounds();
            opt.PickShapes.Add(ps);
        }

        // ---- camera state, in the game's own conventions -----------------------------------
        // (yaw 0 = north/+Y, clockwise; positive pitch looks down) — so P prints a line that
        // pastes straight back into --eye/--yaw/--pitch, and a getpos pastes in mentally.
        var eye = cam.Eye;
        var d0 = cam.Look - cam.Eye;
        float yaw = (float.RadiansToDegrees(MathF.Atan2(d0.X, d0.Y)) + 360f) % 360f;
        float pitch = float.RadiansToDegrees(MathF.Atan2(-d0.Z, MathF.Max(MathF.Sqrt(d0.X * d0.X + d0.Y * d0.Y), 1e-3f)));
        float fov = cam.FovDeg;
        float speed = opt.FlySpeed;             // u/s; scroll adjusts, shift x5, ctrl x0.2
        float lookSens = opt.LookSens;          // degrees per pixel; , and . adjust
        const float DefaultSens = 0.15f;        // only so the HUD can stay quiet at the default

        var camera = new OrbitCamera
        {
            FieldOfView = fov,
            NearPlane = NearPlane,
            FarPlane = FarPlane,
            MaxDistance = 1_000_000f,
        };
        Vector3 Forward()
        {
            float yr = float.DegreesToRadians(yaw), pr = float.DegreesToRadians(pitch);
            return new Vector3(MathF.Sin(yr) * MathF.Cos(pr), MathF.Cos(yr) * MathF.Cos(pr), -MathF.Sin(pr));
        }
        void SyncCamera()
        {
            // The exact first-person-on-OrbitCamera expression the static path uses, including
            // the off-vertical clamp (LookAt with UnitY degenerates straight up/down).
            var look = eye + Forward() * 512f;
            var eyeY = TexturedScene.YUp(eye); var lookY = TexturedScene.YUp(look);
            var off = eyeY - lookY;
            float dist = MathF.Max(off.Length(), 1f);
            camera.Target = new OpenTK.Mathematics.Vector3(lookY.X, lookY.Y, lookY.Z);
            camera.Distance = dist;
            camera.Elevation = Math.Clamp(float.RadiansToDegrees(MathF.Asin(off.Y / dist)), -89.5f, 89.5f);
            camera.Azimuth = float.RadiansToDegrees(MathF.Atan2(off.X, off.Z));
        }

        string Pose() => $"eye {eye.X:0.##},{eye.Y:0.##},{eye.Z:0.##} yaw {yaw:0.#}° pitch {pitch:0.#}° fov {fov:0.#}°";
        string PoseArgs() => $"--eye {eye.X:0.##},{eye.Y:0.##},{eye.Z:0.##} --yaw {yaw:0.#} --pitch {pitch:0.#} --fov {fov:0.#}";

        // The route's slot on the status bar. Silent when the run carries no route, and it names
        // the path — a count of waypoints with no sentence attached says nothing about which
        // walk is on screen when a worklist holds several.
        string RouteHud() => opt.RouteCount == 0 ? ""
            : $" — route {opt.RouteCount} wpt{(opt.ShowRoute ? "" : ", HIDDEN")}"
            + (opt.RouteLabel.Length > 0 ? $" ({opt.RouteLabel})" : "");

        // ---- navmesh display mode (N cycles) ------------------------------------------------
        // The sheet and the wire are both resident; the mode decides which of them draws. The
        // sheet's alpha is the fiddly part: the material shader multiplies the baked VERTEX
        // colour alpha by MaterialAlpha, and the static path bakes the SAME number into both —
        // so a mode's on-screen alpha is its value SQUARED. The sheet here is baked once, at the
        // "filled" value, which makes MaterialAlpha = target²/baked the setting that reproduces
        // another mode's pixels exactly rather than merely approximately.
        static float FillAlpha(float target, float baked) => target * target / baked;
        bool hasNav = opt.NavMode != "off";
        string navMode = opt.NavMode;
        void ApplyNav()
        {
            bool fill = navMode is "filled" or "both";
            bool wire = navMode is "wire" or "both";
            float healthy = fill ? FillAlpha(navMode == "both" ? 0.16f : 0.35f, 0.35f) : 0f;
            float finding = fill ? FillAlpha(navMode == "both" ? 0.36f : 0.55f, 0.55f) : 0f;
            foreach (var m in opt.NavFill) { m.IsRendering = fill; m.MaterialAlpha = healthy; }
            foreach (var m in opt.NavFillRed) { m.IsRendering = fill; m.MaterialAlpha = finding; }
            opt.ShowNavWire = wire;
            // The finding is outlined in EVERY mode that draws navmesh at all — the static
            // path's rule, kept here so a mode switch cannot lose the thing being examined.
            opt.ShowNavRedWire = navMode != "off";
        }
        ApplyNav();

        // ---- live height clip ([ ] ceiling, ; ' floor, \ off) -------------------------------
        // A shape is hidden when its whole z-span sits outside the band. Both ends start
        // DISABLED and, on first touch, at the camera's own z — "clip off everything above my
        // head" is then one keypress from wherever the flight has reached, which is the whole
        // reason the control exists.
        float? clipLo = null, clipHi = null;
        int clipHidden = 0;
        const float ClipRate = 512f;            // u/s while held; the shift/ctrl multiplier applies
        var clippable = opt.PickShapes.Where(p => p.Mesh is not null).ToArray();
        void ApplyClip()
        {
            float lo = clipLo ?? float.NegativeInfinity, hi = clipHi ?? float.PositiveInfinity;
            int hidden = 0;
            foreach (var s in clippable)
            {
                bool show = s.Max.Y >= lo && s.Min.Y <= hi;     // viewer Y IS Skyrim z
                s.Mesh!.IsRendering = show;
                if (!show) hidden++;
            }
            clipHidden = hidden;
        }
        string ClipLabel() =>
            (clipLo, clipHi) switch
            {
                (null, null) => "off",
                ({ } lo, null) => $"z >= {lo:0} ({clipHidden:N0} hidden)",
                (null, { } hi) => $"z <= {hi:0} ({clipHidden:N0} hidden)",
                ({ } lo, { } hi) => $"z {lo:0}..{hi:0} ({clipHidden:N0} hidden)",
            };
        void PrintClip()
        {
            if (clipLo is null && clipHi is null) { Console.WriteLine("   clip: off — every static shape drawn"); return; }
            Console.WriteLine($"   clip: {ClipLabel()} of {clippable.Length:N0} static shape(s)"
                            + (clipHi is { } ch2 ? $"  (static equivalent: --cull-above {ch2:0} — triangle-exact, unlike this shape-level band)" : ""));
        }

        // ---- worklist: PageUp/PageDown step the shots, Ctrl+ steps the cells ---------------
        // A worklist entry IS a camera pose, so stepping shots inside one cell costs nothing:
        // everything is resident and only eye/yaw/pitch/fov change. The label travels with it,
        // because a pose with no sentence attached is a coordinate, not a thing to look at.
        var wl = opt.Worklist;
        var shots = wl is not null && opt.WorklistCell < wl.Cells.Count
                  ? wl.Cells[opt.WorklistCell].Shots : (IReadOnlyList<WorklistShot>)Array.Empty<WorklistShot>();
        int shotIdx = shots.Count > 0 ? Math.Clamp(opt.WorklistShot, 0, shots.Count - 1) : 0;

        string WorklistHud()
        {
            if (wl is null || shots.Count == 0) return "";
            return $"[cell {opt.WorklistCell + 1}/{wl.Cells.Count} · shot {shotIdx + 1}/{shots.Count}] {shots[shotIdx].Label} — ";
        }

        void ApplyShot(int i, bool announce)
        {
            if (shots.Count == 0) return;
            shotIdx = Math.Clamp(i, 0, shots.Count - 1);
            var sh = shots[shotIdx];
            eye = new Vector3(sh.Eye[0], sh.Eye[1], sh.Eye[2]);
            (yaw, pitch) = WorklistIo.Aim(sh);
            fov = sh.Fov;
            camera.FieldOfView = fov;               // nothing else in the loop touches fov, so a shot's own must be pushed
            if (sh.Nav is { } nm && hasNav) { navMode = nm; ApplyNav(); }
            if (sh.CullAbove is { } ca)
            {
                // The worklist's cullAbove is the STATIC render's triangle-exact knife. The live
                // band is shape-granular, so this reproduces the intent, not the pixels — say so
                // rather than letting the two quietly disagree.
                clipHi = ca; ApplyClip();
            }
            if (!announce) return;
            Console.WriteLine($"   shot {shotIdx + 1}/{shots.Count}: {sh.Label}");
            if (sh.Note is { Length: > 0 } nt) Console.WriteLine($"      {nt}");
            Console.WriteLine($"      {PoseArgs()}");
            if (sh.CullAbove is { } ca2) Console.WriteLine($"      clip ceiling set to z {ca2:0} (shape-granular; the static render's --cull-above {ca2:0} is triangle-exact)");
        }

        // Ask the host for another cell. The loop cannot build one, so it closes and reports.
        void RequestCell(int delta)
        {
            if (wl is null) return;
            int want = opt.WorklistCell + delta;
            if (want < 0 || want >= wl.Cells.Count)
            { Console.WriteLine($"   worklist: already at the {(delta < 0 ? "first" : "last")} cell ({wl.Cells.Count} in this list)"); return; }
            if (!opt.CanSwitchCell)
            { Console.WriteLine("   worklist: this run holds one cell only — cell switching needs the whole worklist, not a single --worklist cell."); return; }
            // Backwards lands on the PREVIOUS cell's LAST shot, the way turning a page back does.
            int shot = delta < 0 ? Math.Max(wl.Cells[want].Shots.Count - 1, 0) : 0;
            Console.WriteLine($"   worklist: cell {want + 1}/{wl.Cells.Count} '{wl.Cells[want].Cell}' — rebuilding the scene...");
            opt.CellSwitch = (want, shot);
            gw.Close();
        }

        // ---- hover picking -----------------------------------------------------------------
        (PickShape Shape, int LocalTri, float T, Vector3 HitY)? hover = null;
        // The locked pick: what LMB pinned to the HUD, and the line it rendered to at the moment
        // of the click. Non-null suspends hovering entirely.
        (PickShape Shape, int LocalTri, float T, Vector3 HitY)? locked = null;
        string lockedLine = "";
        // A lock outlives a mode switch, and it should — it says "keep showing me this one". But
        // the clip or N can then hide the very thing it names, and a readout for something not
        // on screen has to SAY so rather than quietly keep asserting it.
        bool Drawn(PickShape s) => s.Mesh is not { IsRendering: false }
                                && !(s.Kind == PickKind.Navmesh && navMode == "off");
        string LockedHud() => locked is { } l && !Drawn(l.Shape)
            ? lockedLine + "   [not drawn now]" : lockedLine;
        // Set when something OTHER than the mouse changed what is under the cursor, so a hover
        // answer refreshes even with the mouse held still — hold [ until a ceiling melts away
        // and the bar must stop naming that ceiling.
        bool pickDirty = false;

        (PickShape Shape, int LocalTri, float T, Vector3 HitY)? PickAt(float mx, float my)
        {
            int w = gw.ClientSize.X, h = gw.ClientSize.Y;
            if (w <= 0 || h <= 0) return null;
            var (o, dir) = camera.ScreenPointToRay(mx, my, w, h);
            var ro = new Vector3(o.X, o.Y, o.Z);
            var rd = new Vector3(dir.X, dir.Y, dir.Z);

            // AABB shortlist first, sorted by entry distance so triangle testing can stop as
            // soon as the best hit is nearer than the next box.
            var candidates = new List<(PickShape S, float T0)>();
            foreach (var s in opt.PickShapes)
            {
                // The ray may only hit what the frame is actually SHOWING. A shape the height
                // clip removed, or navmesh with the display off, is not under the cursor as far
                // as the person looking at the window is concerned — naming it would put the HUD
                // in contradiction with the picture.
                if (s.Mesh is { IsRendering: false }) continue;
                if (s.Kind == PickKind.Navmesh && navMode == "off") continue;
                if (RayAabb(ro, rd, s.Min, s.Max, out var t0)) candidates.Add((s, t0));
            }
            candidates.Sort((a, b) => a.T0.CompareTo(b.T0));

            (PickShape Shape, int LocalTri, float T, Vector3 HitY)? best = null;
            foreach (var (s, t0) in candidates)
            {
                if (best is { } b0 && t0 > b0.T) break;
                var v = s.Verts; var tr = s.Tris;
                for (int i = 0; i + 2 < tr.Length; i += 3)
                    if (RayTri(ro, rd, v[tr[i]], v[tr[i + 1]], v[tr[i + 2]], out var t)
                        && (best is not { } bb || t < bb.T))
                        best = (s, i / 3, t, ro + rd * t);
            }
            return best;
        }

        // fromEye adds the distance to the hit, which is only true for the camera that did the
        // picking — a LOCKED line outlives that camera, so it reports the hit POINT and drops
        // the range rather than displaying a number that quietly goes stale as you fly away.
        string HoverLine((PickShape Shape, int LocalTri, float T, Vector3 HitY)? hit, bool fromEye = true)
        {
            if (hit is not { } hv) return "hover: nothing under the cursor (hold RMB to look, WASD to fly)";
            var ws = new Vector3(hv.HitY.X, -hv.HitY.Z, hv.HitY.Y);   // Y-up back to Skyrim
            var s = hv.Shape;
            string tail = fromEye ? $" — hit {ws.X:0},{ws.Y:0},{ws.Z:0}, {hv.T:0} u"
                                  : $" — at {ws.X:0},{ws.Y:0},{ws.Z:0}";
            switch (s.Kind)
            {
                case PickKind.Ref:
                    var r = s.Ref;
                    string ed = r.BaseEditorId is { Length: > 0 } e ? e : "(no EditorID)";
                    return $"REFR {r.RefKey} = FormID {opt.RuntimeId(r.RefKey)} — {ed} — {r.MeshRelPath}{tail}";
                case PickKind.Navmesh:
                    int ti = s.NavTriIds![hv.LocalTri];
                    string red = opt.RedNav.Contains(s.NavKey) ? " [RED — the finding]" : "";
                    return $"NAVM {s.NavKey} = FormID {opt.RuntimeId(s.NavKey)} tri {ti}{red}{tail}";
                default:
                    string desc = opt.TerrainDescribe?.Invoke(ws.X, ws.Y)
                               ?? (s.TerrainTex.Length > 0 ? s.TerrainTex : "(untextured fallback)");
                    return $"terrain — {desc}{tail}";
            }
        }

        void PrintDetail((PickShape Shape, int LocalTri, float T, Vector3 HitY) hv)
        {
            var ws = new Vector3(hv.HitY.X, -hv.HitY.Z, hv.HitY.Y);
            var s = hv.Shape;
            switch (s.Kind)
            {
                case PickKind.Ref:
                    var r = s.Ref;
                    Console.WriteLine($"   picked REFR {r.RefKey} = FormID {opt.RuntimeId(r.RefKey)}"
                                    + $"{(r.Highlight ? "  [highlighted]" : "")}{(r.Marker ? "  [editor marker]" : "")}");
                    Console.WriteLine($"      base {r.BaseKey} = FormID {opt.RuntimeId(r.BaseKey)}"
                                    + $"  {r.BaseEditorId ?? "(no EditorID)"}");
                    Console.WriteLine($"      mesh {r.MeshRelPath}");
                    Console.WriteLine($"      placed at {r.Position.X:0},{r.Position.Y:0},{r.Position.Z:0} scale {r.Scale:0.00}"
                                    + $"  — hit at {ws.X:0},{ws.Y:0},{ws.Z:0}, {hv.T:0} u from the eye");
                    break;
                case PickKind.Navmesh:
                    int ti = s.NavTriIds![hv.LocalTri];
                    Console.WriteLine($"   picked NAVM {s.NavKey} = FormID {opt.RuntimeId(s.NavKey)} tri {ti}"
                                    + $"{(opt.RedNav.Contains(s.NavKey) ? "  [RED — the finding]" : "")}"
                                    + $"  — hit at {ws.X:0},{ws.Y:0},{ws.Z:0}  (--red \"{s.NavKey}:{ti}\")");
                    break;
                default:
                    Console.WriteLine($"   picked terrain at {ws.X:0},{ws.Y:0},{ws.Z:0} — "
                                    + (opt.TerrainDescribe?.Invoke(ws.X, ws.Y)
                                       ?? (s.TerrainTex.Length > 0 ? s.TerrainTex : "(untextured fallback)")));
                    break;
            }
        }

        // ---- HUD: a caption-style bar at the top of the frame ------------------------------
        // Same visual language as the burned-in PNG caption (dark bar, bold white text), drawn
        // as a textured quad. Rebuilt only when its text or the window width changes.
        //
        // SIZE. 30 px — twice the 15 px the PNG caption uses at 1280 wide. The bar is read at a
        // glance while flying, from further back than a saved image is, and a FormID at 15 px on
        // a 1440-wide window was not legible. Because the text is now big enough to run off the
        // edge (a mesh path is long), the bar WRAPS and grows to fit instead of clipping, which
        // is why its height is computed per rebuild rather than fixed.
        const float HudFontPx = 30f, HudLineH = 44f, HudPadX = 12f, HudPadY = 9f;
        int hudProg = HudShader();
        int hudVao = GL.GenVertexArray();                      // attribute-less draw still needs a VAO
        int hudTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, hudTex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        Font? hudFont = null;
        foreach (var famName in new[] { "Segoe UI", "Arial", "Verdana" })
            if (SystemFonts.TryGet(famName, out var fam)) { hudFont = fam.CreateFont(HudFontPx, FontStyle.Bold); break; }
        if (hudFont is null)
            foreach (var fam in SystemFonts.Families) { hudFont = fam.CreateFont(HudFontPx, FontStyle.Bold); break; }
        string hudLine1 = "hover: (free the cursor and point at an object; RMB-drag to look)";
        string hudLine2 = "";
        string hudBuilt = "";
        int hudBuiltW = 0, hudBarH = (int)(HudPadY * 2 + HudLineH * 2);

        // Greedy word wrap against the real advance width, with a per-character fallback for a
        // single token wider than the bar — which is the common case, not a corner one: a mesh
        // path has no spaces in it.
        List<string> WrapLine(string text, float maxWidth)
        {
            var outLines = new List<string>();
            if (hudFont is null) return outLines;
            var to = new TextOptions(hudFont);
            float Width(string s) => TextMeasurer.MeasureAdvance(s, to).Width;
            var cur = "";
            foreach (var word in text.Split(' '))
            {
                var probe = cur.Length == 0 ? word : cur + " " + word;
                if (Width(probe) <= maxWidth) { cur = probe; continue; }
                if (cur.Length > 0) { outLines.Add(cur); cur = ""; }
                if (Width(word) <= maxWidth) { cur = word; continue; }
                foreach (var ch in word)
                {
                    if (Width(cur + ch) > maxWidth && cur.Length > 0) { outLines.Add(cur); cur = ""; }
                    cur += ch;
                }
            }
            if (cur.Length > 0) outLines.Add(cur);
            return outLines.Count > 0 ? outLines : new List<string> { "" };
        }

        void RebuildHud(int width)
        {
            if (hudFont is null || width <= 0) return;
            string key = hudLine1 + "\n" + hudLine2;
            if (key == hudBuilt && width == hudBuiltW) return;
            hudBuilt = key; hudBuiltW = width;
            float avail = MathF.Max(32f, width - HudPadX * 2);
            var wrapped1 = WrapLine(hudLine1, avail);
            var wrapped2 = WrapLine(hudLine2, avail);
            hudBarH = (int)(HudPadY * 2 + HudLineH * (wrapped1.Count + wrapped2.Count));
            using var img = new Image<Rgba32>(width, hudBarH);
            var f = hudFont;
            img.Mutate(x =>
            {
                x.Fill(SixLabors.ImageSharp.Color.FromRgba(8, 10, 14, 200), new SixLabors.ImageSharp.RectangleF(0, 0, width, hudBarH));
                for (int i = 0; i < wrapped1.Count; i++)
                    x.DrawText(wrapped1[i], f, SixLabors.ImageSharp.Color.White,
                               new SixLabors.ImageSharp.PointF(HudPadX, HudPadY + HudLineH * i));
                for (int j = 0; j < wrapped2.Count; j++)
                    x.DrawText(wrapped2[j], f, SixLabors.ImageSharp.Color.FromRgb(200, 205, 215),
                               new SixLabors.ImageSharp.PointF(HudPadX, HudPadY + HudLineH * (wrapped1.Count + j)));
            });
            var px = new byte[width * hudBarH * 4];
            img.CopyPixelDataTo(px);
            GL.BindTexture(TextureTarget.Texture2D, hudTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, hudBarH, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, px);
        }
        void DrawHud(int fbW, int fbH)
        {
            if (hudFont is null || fbW <= 0 || fbH <= 0) return;
            RebuildHud(fbW);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.UseProgram(hudProg);
            GL.Uniform1(GL.GetUniformLocation(hudProg, "u_bottom"), 1f - 2f * hudBarH / fbH);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, hudTex);
            GL.Uniform1(GL.GetUniformLocation(hudProg, "u_tex"), 0);
            GL.BindVertexArray(hudVao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            GL.BindVertexArray(0);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
        }

        // ---- screenshot: the frame as rendered, minus the HUD, plus the standard caption ----
        bool wantShot = false;
        void CaptureShot(int fbW, int fbH)
        {
            // The window's back buffer may be multisampled; resolve by blit before reading.
            int fbo = GL.GenFramebuffer(), rb = GL.GenRenderbuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, fbW, fbH);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, rb);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            GL.BlitFramebuffer(0, 0, fbW, fbH, 0, 0, fbW, fbH, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            var px = new byte[fbW * fbH * 4];
            GL.ReadPixels(0, 0, fbW, fbH, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo); GL.DeleteRenderbuffer(rb);

            string pose = Pose();
            string outPath = Path.Combine(opt.OutDir, $"{opt.SafeName}-fly-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            TexturedScene.SaveFrame(px, fbW, fbH,
                new[] { $"{opt.CellLabel} — fly capture — {pose}" },
                Array.Empty<(string, Vector3)>(),
                $"pose: {pose} | cell {opt.CellLabel}",
                outPath);
            Console.WriteLine($"   wrote {outPath}");
            Console.WriteLine($"   replay: {PoseArgs()}");
        }

        void PrintHelp()
        {
            Console.WriteLine("   fly:    WASD move (W/S along the view), E/Space up, Q/C down, hold RMB + mouse to look");
            Console.WriteLine($"   feel:   scroll = fly speed (now {speed:0} u/s), , and . = look sensitivity (now {lookSens:0.000} deg/px); "
                            + "shift x5, ctrl x0.2 on every rate");
            Console.WriteLine("   pick:   hover identifies under the cursor (HUD); LMB LOCKS that readout and prints full "
                            + "detail; LMB again unlocks");
            Console.WriteLine($"   nav:    N cycles the navmesh display off -> filled -> wire -> both{(hasNav ? $" (now {navMode})" : " — this run has none")}");
            Console.WriteLine($"   clip:   [ and ] move the CEILING, ; and ' move the FLOOR (first touch starts at the camera's z), "
                            + $"\\ = clips off (now {ClipLabel()})");
            Console.WriteLine($"   route:  R shows/hides the walking route"
                            + (opt.RouteCount > 0 ? $" ({opt.RouteCount} waypoint(s), now {(opt.ShowRoute ? "shown" : "hidden")})"
                                                  : " — this run has none"));
            if (wl is not null)
                Console.WriteLine($"   shots:  PageUp/PageDown step this cell's shots (now {shotIdx + 1}/{Math.Max(shots.Count, 1)}), "
                                + $"Ctrl+PageUp/PageDown step the worklist's cells (now {opt.WorklistCell + 1}/{wl.Cells.Count})"
                                + (opt.CanSwitchCell ? "" : " — cell switching is off in this run"));
            Console.WriteLine("   out:    P = print --eye/--yaw/--pitch, F12 = save PNG, H/F1 = this help, Esc = quit");
        }

        // ---- window setup and the loop -----------------------------------------------------
        gw.Title = $"viewprobe — {opt.CellLabel}";
        gw.VSync = VSyncMode.On;
        gw.CursorState = CursorState.Normal;
        gw.IsVisible = true;
        gw.Focus();
        SyncCamera();
        Console.WriteLine($"   interactive: {opt.PickShapes.Count:N0} pickable shape(s) "
                        + $"({opt.PickShapes.Count(p => p.Kind == PickKind.Ref):N0} ref, "
                        + $"{opt.PickShapes.Count(p => p.Kind == PickKind.Navmesh):N0} navmesh, "
                        + $"{opt.PickShapes.Count(p => p.Kind == PickKind.Terrain):N0} terrain)"
                        + $"; {clippable.Length:N0} static shape(s) clippable by height");
        if (hasNav)
            Console.WriteLine($"   navmesh display: {navMode} (entry mode) — sheet and wire are BOTH resident, "
                            + "so N switches between them with no rebuild");
        if (opt.RouteCount > 0)
            Console.WriteLine($"   route: {opt.RouteCount} waypoint(s) drawn as overlay geometry"
                            + (opt.RouteLabel.Length > 0 ? $" — {opt.RouteLabel}" : "") + "; R hides it");
        PrintHelp();

        // Headless smoke knobs: VP_FLY_FRAMES=N renders N frames then saves a capture and
        // closes (the loop is testable without a hand on the mouse); VP_FLY_PICK=x,y runs one
        // programmatic pick at those client coordinates before closing.
        int autoFrames = int.TryParse(Environment.GetEnvironmentVariable("VP_FLY_FRAMES"), out var af) ? af : 0;
        (float X, float Y)? autoPick = null;
        if (Environment.GetEnvironmentVariable("VP_FLY_PICK") is { } aps)
        {
            var p = aps.Split(',');
            if (p.Length == 2 && float.TryParse(p[0], out var ax) && float.TryParse(p[1], out var ay))
                autoPick = (ax, ay);
        }
        int frameCount = 0;

        // The same headless family, extended to the three things the keys now drive. Without
        // these the new controls could only be exercised by hand, and the HUD — which the F12
        // capture deliberately excludes — could not be seen in a test at all.
        bool autoLock = Environment.GetEnvironmentVariable("VP_FLY_LOCK") == "1";
        bool hudInShot = Environment.GetEnvironmentVariable("VP_FLY_HUD") == "1";

        // The worklist's opening shot is applied BEFORE the VP_FLY_* overrides, so a test that
        // pins the nav mode or the clip still wins over whatever that shot asked for. VP_FLY_SHOT
        // selects a shot headlessly — the PageUp/PageDown key's effect, without a keyboard.
        if (Environment.GetEnvironmentVariable("VP_FLY_SHOT") is { Length: > 0 } shotSpec)
        {
            if (int.TryParse(shotSpec, out var wantShotIdx) && shots.Count > 0)
                shotIdx = Math.Clamp(wantShotIdx - 1, 0, shots.Count - 1);          // 1-based, like the HUD
            else Console.WriteLine($"   [auto] VP_FLY_SHOT '{shotSpec}' ignored: not a shot number in this cell");
        }
        if (shots.Count > 0) ApplyShot(shotIdx, announce: true);

        // VP_FLY_CELL=+1|-1: after the capture, ask for the next/previous worklist cell instead of
        // just closing — Ctrl+PageUp/PageDown's effect, headlessly. Without it the multi-cell
        // rebuild is the one path in this feature no test can reach, which is also the one that
        // tears down a GL context and builds another.
        int autoCell = int.TryParse(Environment.GetEnvironmentVariable("VP_FLY_CELL"), out var ac) ? ac : 0;
        void AutoFinish()
        {
            if (autoCell != 0 && wl is not null)
            {
                Console.WriteLine($"   [auto] VP_FLY_CELL={autoCell:+0;-0}");
                RequestCell(autoCell);                    // sets CellSwitch and closes
                if (opt.CellSwitch is not null) return;   // refused (no such cell): fall through and close
            }
            gw.Close();
        }
        if (Environment.GetEnvironmentVariable("VP_FLY_NAV") is { Length: > 0 } navSpec)
        {
            string want = navSpec.ToLowerInvariant();
            if (want is not ("off" or "filled" or "wire" or "both"))
                Console.WriteLine($"   [auto] VP_FLY_NAV '{want}' is not off|filled|wire|both — ignored");
            else if (!hasNav && want != "off")
                Console.WriteLine($"   [auto] VP_FLY_NAV={want} ignored: this run carries no navmesh");
            else { navMode = want; ApplyNav(); Console.WriteLine($"   [auto] nav display: {navMode}"); }
        }
        // VP_FLY_ROUTE=on|off: the R key's effect, headlessly. Every other live control has one of
        // these, for the reason this file already states — without it the toggle could only be
        // exercised by hand, and a control no test can reach is a control nothing checks.
        if (Environment.GetEnvironmentVariable("VP_FLY_ROUTE") is { Length: > 0 } routeSpec)
        {
            string want = routeSpec.ToLowerInvariant();
            if (want is not ("on" or "off"))
                Console.WriteLine($"   [auto] VP_FLY_ROUTE '{want}' is not on|off — ignored");
            else if (opt.RouteCount == 0)
                Console.WriteLine($"   [auto] VP_FLY_ROUTE={want} ignored: this run carries no route");
            else { opt.ShowRoute = want == "on"; Console.WriteLine($"   [auto] VP_FLY_ROUTE={want}"); }
        }
        if (Environment.GetEnvironmentVariable("VP_FLY_CLIP") is { Length: > 0 } clipSpec)
        {
            // "lo,hi"; either side may be empty for "that end off" — VP_FLY_CLIP=",2048" is a
            // ceiling with no floor, which is the top-floor case.
            var p = clipSpec.Split(',');
            if (p.Length != 2) Console.WriteLine($"   [auto] VP_FLY_CLIP '{clipSpec}' is not \"lo,hi\" — ignored");
            else
            {
                clipLo = float.TryParse(p[0], out var lo) ? lo : null;
                clipHi = float.TryParse(p[1], out var hi) ? hi : null;
                if (clipLo is { } l && clipHi is { } h && l > h) (clipLo, clipHi) = (h, l);
                ApplyClip();
                PrintClip();
            }
        }

        var titleClock = Stopwatch.StartNew();
        var pickClock = Stopwatch.StartNew();
        OpenTK.Mathematics.Vector2 lastPickPos = new(-1000f, -1000f);

        // VP_FLY_FRAMES is a TEST, and a test must not read the machine's live keyboard and
        // mouse. It did, and the window takes focus when it opens, so two identical smoke runs
        // captured DIFFERENT frames — measured 2026-08-28: one drifted 138 u in x and 218 u in
        // z before frame 30, because whatever was being typed at the desk went into the window.
        // Under VP_FLY_FRAMES the loop is therefore input-deaf: the camera stays exactly where
        // the command line put it, and a capture is a function of the arguments alone.
        bool autoRun = autoFrames > 0;

        gw.UpdateFrame += e =>
        {
            var kb = gw.KeyboardState;
            var ms = gw.MouseState;
            float dt = (float)e.Time;

            if (autoRun)
            {
                SyncCamera();
                if (locked is not null) hudLine1 = LockedHud();
                hudLine2 = $"{WorklistHud()}{Pose()} — speed {speed:0} u/s"
                         + (hasNav ? $" — nav {navMode}" : "")
                         + RouteHud()
                         + (clipLo is not null || clipHi is not null ? $" — clip {ClipLabel()}" : "")
                         + " — H help";
                return;
            }

            bool looking = ms.IsButtonDown(MouseButton.Right);
            gw.CursorState = looking ? CursorState.Grabbed : CursorState.Normal;
            if (looking)
            {
                yaw = (yaw + ms.Delta.X * lookSens + 360f) % 360f;
                pitch = Math.Clamp(pitch + ms.Delta.Y * lookSens, -89f, 89f);
            }

            float mult = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift) ? 5f
                       : kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl) ? 0.2f : 1f;
            float step = speed * mult * dt;
            var fwd = Forward();
            float yr = float.DegreesToRadians(yaw);
            var right = new Vector3(MathF.Cos(yr), -MathF.Sin(yr), 0f);
            if (kb.IsKeyDown(Keys.W)) eye += fwd * step;
            if (kb.IsKeyDown(Keys.S)) eye -= fwd * step;
            if (kb.IsKeyDown(Keys.A)) eye -= right * step;
            if (kb.IsKeyDown(Keys.D)) eye += right * step;
            if (kb.IsKeyDown(Keys.E) || kb.IsKeyDown(Keys.Space)) eye.Z += step;
            if (kb.IsKeyDown(Keys.Q) || kb.IsKeyDown(Keys.C)) eye.Z -= step;
            if (MathF.Abs(ms.ScrollDelta.Y) > 0f)
                speed = Math.Clamp(speed * MathF.Pow(1.25f, ms.ScrollDelta.Y), 20f, 20000f);
            // Look sensitivity, on the same 1.25x ladder the scroll uses for speed. Printed on
            // change: the number that finally feels right is worth carrying to --look-sens.
            if (kb.IsKeyPressed(Keys.Comma) || kb.IsKeyPressed(Keys.Period))
            {
                lookSens = Math.Clamp(lookSens * (kb.IsKeyPressed(Keys.Period) ? 1.25f : 1f / 1.25f), 0.01f, 2f);
                Console.WriteLine($"   look sensitivity: {lookSens:0.000} deg/px  (--look-sens {lookSens:0.###})");
            }

            // ---- navmesh display ------------------------------------------------------------
            if (kb.IsKeyPressed(Keys.N))
            {
                if (!hasNav)
                    Console.WriteLine("   nav: this run carries no navmesh (--nav off, or none in the cell) — nothing to cycle");
                else
                {
                    navMode = navMode switch { "off" => "filled", "filled" => "wire", "wire" => "both", _ => "off" };
                    ApplyNav();
                    pickDirty = true;
                    Console.WriteLine($"   nav: {navMode}"
                                    + (navMode == "off" ? "  (the finding's outline goes with it)" : ""));
                }
            }

            // ---- the route ------------------------------------------------------------------
            // The same shape as N: a run with nothing to toggle says so rather than appearing to
            // work. What is UNDER the path is the question a drawn path stops you answering.
            if (kb.IsKeyPressed(Keys.R))
            {
                if (opt.RouteCount == 0)
                    Console.WriteLine("   route: this run carries no route (--route X,Y, or a worklist shot's route) — nothing to toggle");
                else
                {
                    opt.ShowRoute = !opt.ShowRoute;
                    Console.WriteLine($"   route: {(opt.ShowRoute ? "shown" : "hidden")}  ({opt.RouteCount} waypoint(s)"
                                    + (opt.RouteLabel.Length > 0 ? $" — {opt.RouteLabel}" : "") + ")");
                }
            }

            // ---- height clip ----------------------------------------------------------------
            // Held keys, not taps: sweeping a ceiling through a two-storey interior is hundreds
            // of units, and one tap per 4 u would be unusable. The first frame a key is down
            // seeds that end at the camera's own z (?? = "not set yet"), so the gesture is
            // "stand on the floor you want and hold [".
            bool ceilDown = kb.IsKeyDown(Keys.LeftBracket), ceilUp = kb.IsKeyDown(Keys.RightBracket);
            bool floorDown = kb.IsKeyDown(Keys.Semicolon), floorUp = kb.IsKeyDown(Keys.Apostrophe);
            bool clipDirty = false;
            if (ceilDown ^ ceilUp)
            {
                clipHi ??= eye.Z;
                clipHi = MathF.Max(clipHi.Value + (ceilUp ? 1f : -1f) * ClipRate * mult * dt,
                                   clipLo ?? float.NegativeInfinity);
                clipDirty = true;
            }
            if (floorDown ^ floorUp)
            {
                clipLo ??= eye.Z;
                clipLo = MathF.Min(clipLo.Value + (floorUp ? 1f : -1f) * ClipRate * mult * dt,
                                   clipHi ?? float.PositiveInfinity);
                clipDirty = true;
            }
            if (kb.IsKeyPressed(Keys.Backslash)) { clipLo = clipHi = null; clipDirty = true; PrintClip(); }
            if (clipDirty) { ApplyClip(); pickDirty = true; }
            // On release, echo the band — the console is where a number gets copied from, and
            // --cull-above is what the static deliverable needs.
            if (kb.IsKeyReleased(Keys.LeftBracket) || kb.IsKeyReleased(Keys.RightBracket)
             || kb.IsKeyReleased(Keys.Semicolon) || kb.IsKeyReleased(Keys.Apostrophe)) PrintClip();

            // Worklist paging. Ctrl is the cell modifier, so the two live one keypress apart:
            // PageDown walks the shots of this cell, Ctrl+PageDown walks to the next cell.
            if (kb.IsKeyPressed(Keys.PageUp) || kb.IsKeyPressed(Keys.PageDown))
            {
                int dir = kb.IsKeyPressed(Keys.PageDown) ? 1 : -1;
                bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
                if (wl is null) Console.WriteLine("   PageUp/PageDown step a worklist; this run was not given --worklist.");
                else if (ctrl) RequestCell(dir);
                else if (shots.Count == 0) Console.WriteLine("   worklist: this cell has no shots.");
                else if (shotIdx + dir < 0 || shotIdx + dir >= shots.Count)
                    Console.WriteLine($"   worklist: {(dir < 0 ? "first" : "last")} shot of this cell"
                                    + (opt.CanSwitchCell ? " — Ctrl+PageUp/PageDown for the previous/next cell" : ""));
                else ApplyShot(shotIdx + dir, announce: true);
            }

            if (kb.IsKeyPressed(Keys.P))
            {
                Console.WriteLine($"   pose: {Pose()}");
                Console.WriteLine($"   args: {PoseArgs()}");
            }
            if (kb.IsKeyPressed(Keys.F12)) wantShot = true;
            if (kb.IsKeyPressed(Keys.H) || kb.IsKeyPressed(Keys.F1)) PrintHelp();
            if (kb.IsKeyPressed(Keys.Escape)) gw.Close();

            SyncCamera();

            // ---- hover, and the LOCK ---------------------------------------------------------
            // Locked, the HUD keeps showing ONE object's identity while the camera moves — which
            // is the point: reading a FormKey off a wall you are also trying to fly around meant
            // holding the cursor perfectly still. So picking is suspended while locked (the
            // readout cannot drift, and the raycast cannot cost anything), and the next LMB
            // anywhere releases it, hit or miss.
            if (!looking)
            {
                if (locked is null)
                {
                    var mp = gw.MousePosition;
                    bool moved = (mp - lastPickPos).LengthSquared > 4f;
                    if ((moved || pickDirty) && pickClock.ElapsedMilliseconds > 60)
                    {
                        pickClock.Restart();
                        pickDirty = false;
                        lastPickPos = mp;
                        hover = PickAt(mp.X, mp.Y);
                        hudLine1 = HoverLine(hover);
                    }
                    if (ms.IsButtonPressed(MouseButton.Left) && hover is { } hv)
                    {
                        locked = hv;
                        lockedLine = "LOCKED — " + HoverLine(hv, fromEye: false) + "   (LMB anywhere to unlock)";
                        hudLine1 = LockedHud();
                        PrintDetail(hv);
                    }
                }
                else if (ms.IsButtonPressed(MouseButton.Left))
                {
                    locked = null; hover = null;
                    lastPickPos = new OpenTK.Mathematics.Vector2(-1000f, -1000f);   // force the next hover to re-pick
                    hudLine1 = "unlocked — hover to identify";
                    Console.WriteLine("   unlocked");
                }
            }
            else if (locked is null)
                hudLine1 = "looking (release RMB to hover-pick)";

            if (locked is not null) hudLine1 = LockedHud();

            if (hudLine2.Length == 0 || titleClock.ElapsedMilliseconds > 150)
            {
                titleClock.Restart();
                // Defaults stay quiet: sensitivity and the clip only take a slot on the bar once
                // they are no longer at their entry value, so the common line stays one row.
                hudLine2 = $"{WorklistHud()}{Pose()} — speed {speed * mult:0} u/s"
                         + (MathF.Abs(lookSens - DefaultSens) > 1e-4f ? $" — look {lookSens:0.000}" : "")
                         + (hasNav ? $" — nav {navMode}" : "")
                         + RouteHud()
                         + (clipLo is not null || clipHi is not null ? $" — clip {ClipLabel()}" : "")
                         + " — H help";
            }
        };

        gw.RenderFrame += e =>
        {
            int w = gw.FramebufferSize.X, h = gw.FramebufferSize.Y;
            if (w <= 0 || h <= 0) return;                        // minimized
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            renderer.Render(camera, w, h);
            var view = camera.GetViewMatrix();
            var proj = camera.GetProjectionMatrix((float)w / h);
            overlayDraw?.Invoke(view, proj);
            // F12 grabs the frame WITHOUT the HUD — the PNG carries the standard burned caption
            // instead, so a capture looks like a render and not like a screenshot of a tool.
            // VP_FLY_HUD=1 moves the grab to after the bar, which is the only way a headless
            // test can see the bar at all.
            if (wantShot && !hudInShot) { wantShot = false; CaptureShot(w, h); if (autoFrames > 0) AutoFinish(); }
            DrawHud(w, h);
            if (wantShot && hudInShot) { wantShot = false; CaptureShot(w, h); if (autoFrames > 0) AutoFinish(); }
            gw.SwapBuffers();

            // Smoke path: run the pick, then request a capture — which the NEXT frame takes at
            // the pre-HUD point above, exactly like a hand-pressed F12 — and close after it.
            if (autoFrames > 0 && ++frameCount == autoFrames)
            {
                if (autoPick is { } ap)
                {
                    var hit = PickAt(ap.X, ap.Y);
                    Console.WriteLine($"   [auto] pick at {ap.X:0},{ap.Y:0}: {HoverLine(hit)}");
                    if (hit is { } hv)
                    {
                        PrintDetail(hv);
                        // VP_FLY_LOCK: the same state LMB produces, so the locked HUD line is
                        // what a VP_FLY_HUD capture shows.
                        if (autoLock)
                        {
                            locked = hv;
                            lockedLine = "LOCKED — " + HoverLine(hv, fromEye: false) + "   (LMB anywhere to unlock)";
                            hudLine1 = LockedHud();
                            Console.WriteLine("   [auto] locked");
                        }
                    }
                }
                wantShot = true;
            }
        };

        gw.Run();

        GL.DeleteProgram(hudProg);
        GL.DeleteVertexArray(hudVao);
        GL.DeleteTexture(hudTex);
        Console.WriteLine($"   interactive session ended at {Pose()}");
        Console.WriteLine($"   replay: {PoseArgs()}");
    }

    // Ray-AABB slab test; tEnter is the entry distance (0 when the origin is inside).
    static bool RayAabb(in Vector3 o, in Vector3 d, in Vector3 mn, in Vector3 mx, out float tEnter)
    {
        tEnter = 0f;
        float t0 = 0f, t1 = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
            float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float lo = i == 0 ? mn.X : i == 1 ? mn.Y : mn.Z;
            float hi = i == 0 ? mx.X : i == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(di) < 1e-12f)
            {
                if (oi < lo || oi > hi) return false;
                continue;
            }
            float inv = 1f / di;
            float ta = (lo - oi) * inv, tb = (hi - oi) * inv;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        tEnter = t0;
        return true;
    }

    // Möller–Trumbore, double-sided (the scene draws plenty of double-sided geometry).
    static bool RayTri(in Vector3 o, in Vector3 d, in Vector3 a, in Vector3 b, in Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > NearPlane;                   // nothing nearer than the near plane is a hit
    }

    static int HudShader()
    {
        const string vs = @"#version 330 core
out vec2 uv;
uniform float u_bottom;   // NDC y of the bar's bottom edge
void main(){
    float x = (gl_VertexID & 1) == 1 ? 1.0 : -1.0;
    bool top = gl_VertexID >= 2;
    float y = top ? 1.0 : u_bottom;
    uv = vec2((x + 1.0) * 0.5, top ? 0.0 : 1.0);
    gl_Position = vec4(x, y, 0.0, 1.0);
}";
        const string fs = @"#version 330 core
in vec2 uv;
uniform sampler2D u_tex;
out vec4 frag;
void main(){ frag = texture(u_tex, uv); }";
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v); Check(v, "vertex");
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f); Check(f, "fragment");
        int p = GL.CreateProgram(); GL.AttachShader(p, v); GL.AttachShader(p, f); GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("hud shader link failed: " + GL.GetProgramInfoLog(p));
        GL.DeleteShader(v); GL.DeleteShader(f);
        return p;

        static void Check(int s, string what)
        {
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"hud {what} shader: {GL.GetShaderInfoLog(s)}");
        }
    }
}
