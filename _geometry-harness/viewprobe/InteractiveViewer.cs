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

enum PickKind { Ref, Navmesh, Terrain, Plane }

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
    public PlaneRender? Plane;                         // Kind==Plane
    /// <summary>Kind==Ref: the occlusion preview (`on`) says a plane hides this ref from the current eye. Folded
    /// into visibility by the clip, so the two knives write one flag and cannot disagree.</summary>
    public bool Culled;
    /// <summary>Kind==Ref: the world-state view (`on`) hides this departed ref. Folded into visibility by the clip like
    /// <see cref="Culled"/>, for the same reason.</summary>
    public bool StateHidden;
    public Vector3 Min, Max;
    /// <summary>Kind==Ref: the GlMesh these triangles were uploaded as, so the height clip can
    /// hide it and the ray can then SKIP it. A pick that names a shape the clip just removed
    /// would have the HUD contradicting the picture. Null for navmesh and terrain, which the
    /// clip never touches.</summary>
    public GlMesh? Mesh;

    /// <summary>Kind==Ref: the projected-covering shell riding on this shape, if it has one. The
    /// clip has to hide it WITH its base — a covering is not a pick shape of its own (hovering
    /// should name the roof, not the snow on it), so without this link clipping a roof away left
    /// its snow hanging in the air. Not part of the bounds or the ray: it is the same triangles
    /// lifted 2 u, so it can only ever answer what the base already answers.</summary>
    public GlMesh? CoverMesh;

    public void ComputeBounds()
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var i in Tris) { mn = Vector3.Min(mn, Verts[i]); mx = Vector3.Max(mx, Verts[i]); }
        Min = mn; Max = mx;
    }
}

/// <summary>One answer from the picking ray: the shape, the triangle, the distance and the point. For a plane
/// hit, <see cref="Behind"/> is the nearest NON-plane surface on the same ray — a picked plane reports the
/// plane, then what it stands in front of, because the second half is what a plane is for.</summary>
sealed record PickHit(PickShape Shape, int LocalTri, float T, Vector3 HitY)
{
    public PickHit? Behind { get; init; }
    /// <summary>Every drawn plane the ray crosses BEYOND this hit, nearest first. A plane behind a wall is
    /// still in the picture — its outline is dashed there — so a hover on the wall names the planes the
    /// ray goes on to cross, rather than pretending the dashes are not under the cursor.</summary>
    public IReadOnlyList<PickHit>? PlanesBeyond { get; init; }
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

    // The BUILD-time --cull-above, if the run carried one. Purely so the loop can SAY so: those
    // triangles were dropped before the window opened and no key can bring them back, while the
    // live clip's "(N hidden)" describes only the shapes the band is holding down right now. With
    // nothing announcing the knife, a scene missing a third of its roofs reads as a finding —
    // which is the exact reading a real missing-geometry bug got in this project (2026-08-30).
    public float? BuildCullAbove;

    // The BUILD-time --nav-z band, for the same reason and with the same force: the triangles of
    // every other storey were never uploaded, so N cycles the modes over one floor's worth of
    // navmesh and no key widens it. Unannounced, a hover that finds nothing over the gallery
    // upstairs reads as missing navmesh instead of as a filter doing its job.
    public (float Z, float Tol)? BuildNavZ;

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

    // ---- the occlusion planes, switched live by L --------------------------------------------
    // The layer is overlay geometry tagged 4, so showing it is a draw-time flag like the wire's;
    // the pick shapes follow the flag, so a hover cannot name a plane the picture does not show.
    public int PlaneCount;                                 // planes resident; 0 = none in this cell
    public bool ShowPlanes;                                // entry state from --planes or the shot
    public string PlaneLegend = "";                        // the caption's colour key, on the status line while shown

    // ---- the occlusion preview, cycled live by O ---------------------------------------------
    // Built by the scene (every drawn ref's bounds and meshes) and re-evaluated by the loop whenever
    // the eye moves: `on` hides what the planes cull from here, `diff` tints it red.
    public OcclusionState? Occlusion;                      // filled by the scene build
    public string OcclusionMode = "off";                   // entry mode from --occlusion or the shot
    public BosViewState? Bos;                              // the Base Object Swapper layer, filled by the scene build
    public StateViewState? State;                          // the world state's paint (--state), filled by the scene build

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
    public bool ReportMode;                               // scene-only window and captures; suppress all text/UI overlays
    public (int Cell, int Shot)? CellSwitch;               // set by Ctrl+PageUp/Down, read by the host after Run returns
}

static class InteractiveViewer
{
    // The textured path's frustum, exactly (near 15 u = the game's fNearDistance; see TexturedScene).
    const float NearPlane = 15f, FarPlane = 131072f;

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

        // Set when something OTHER than the mouse changed what is under the cursor, so a hover
        // answer refreshes even with the mouse held still — hold [ until a ceiling melts away
        // and the bar must stop naming that ceiling. Declared here, ahead of every local function
        // that raises it (the clip, the shots, the occlusion preview), because a local function
        // cannot capture a variable declared below it.
        bool pickDirty = false;

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
                // The band AND the occlusion preview decide together: a shape a plane hides in `on`
                // is written through the same flag the clip writes, so neither knife can undo the other.
                bool show = s.Max.Y >= lo && s.Min.Y <= hi && !s.Culled && !s.StateHidden;     // viewer Y IS Skyrim z
                s.Mesh!.IsRendering = show;
                if (s.CoverMesh is { } cm) cm.IsRendering = show;   // the snow goes with the roof
                if (!show && !(s.Max.Y >= lo && s.Min.Y <= hi)) hidden++;
            }
            clipHidden = hidden;
        }

        // ---- the occlusion preview (O cycles off -> on -> diff) -------------------------------
        // Re-evaluated whenever the eye moves — dozens of planes against thousands of boxes, a few
        // million dot products at most — and pushed through ApplyClip so visibility stays one flag.
        // The hover glow is taken off on every change so it cannot save a tinted material as the
        // mesh's own and hand it back later.
        string occlMode = opt.OcclusionMode;
        string bosMode = opt.Bos?.Mode ?? "off";               // the BOS layer's paint (BosView.cs); B toggles on <-> diff
        string stateMode = opt.State?.Mode ?? "on";            // the world state's paint (StateView.cs); T toggles on <-> diff
        bool hasOccl = opt.Occlusion is { Planes.Count: > 0 };
        void ApplyOcclusion(bool force = false)
        {
            if (!hasOccl || opt.Occlusion is not { } os) return;
            if (!os.Apply(eye, occlMode, ownsVisibility: false, force)) return;
            ApplyClip(); SetGlow(null); pickDirty = true;
        }
        void SetOcclusion(string mode)
        {
            if (!hasOccl) { Console.WriteLine("   occlusion: this cell carries no occlusion planes — nothing to preview"); return; }
            occlMode = mode;
            ApplyOcclusion(force: true);
            Console.WriteLine("   " + opt.Occlusion!.Summary(occlMode));
        }
        string OcclusionLabel() => !hasOccl || occlMode == "off" || opt.Occlusion is not { } os2 ? ""
            : $"OCCLUSION {occlMode}: {os2.CulledCount:N0} ref(s) wholly behind a plane from here — {(occlMode == "on" ? "not drawn" : "tinted red")}";
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
            if (sh.Planes is { } sp && opt.PlaneCount > 0) { opt.ShowPlanes = sp; pickDirty = true; }
            if (sh.Occlusion is { } so && hasOccl) { occlMode = so; ApplyOcclusion(force: true); }
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
        PickHit? hover = null;
        // The locked pick: what LMB pinned. Non-null suspends hovering entirely. Since 2026-08-31
        // it is reported on the CONTROL BAR as three click-to-copy fields rather than as a
        // sentence on the caption — the caption is for the thing under the cursor right now.
        PickHit? locked = null;
        // A lock outlives a mode switch, and it should — it says "keep showing me this one". But
        // the clip or N can then hide the very thing it names, and a readout for something not
        // on screen has to SAY so rather than quietly keep asserting it.
        bool Drawn(PickShape s) => s.Mesh is not { IsRendering: false }
                                && !(s.Kind == PickKind.Navmesh && navMode == "off")
                                && !(s.Kind == PickKind.Plane && !opt.ShowPlanes);
        // Where the last pick was taken from. Reset to nowhere to force the next frame to re-pick
        // — what "unlock" has to do, whether the key, the bar's clear button or a mode change
        // asks for it.
        OpenTK.Mathematics.Vector2 lastPickPos = new(-1000f, -1000f);

        PickHit? PickAt(float mx, float my)
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
                if (s.Kind == PickKind.Plane && !opt.ShowPlanes) continue;
                if (RayAabb(ro, rd, s.Min, s.Max, out var t0)) candidates.Add((s, t0));
            }
            candidates.Sort((a, b) => a.T0.CompareTo(b.T0));

            // Three answers, not one: the nearest hit, the nearest hit that is NOT a plane, and every
            // drawn plane the ray crosses. A plane is translucent by nature — what it stands in front
            // of is half of its identity — so a plane hit carries the surface behind it; and a plane
            // BEHIND the nearest surface is still in the picture (its outline is dashed there), so a
            // hit on a wall carries the planes the ray goes on to cross. Planes are few, so the walk
            // tests all of them; the solid walk still stops at the first box past the best hit.
            PickHit? best = null, solid = null;
            List<PickHit>? planeHits = null;
            foreach (var (s, t0) in candidates)
            {
                bool isPlane = s.Kind == PickKind.Plane;
                if (!isPlane && solid is { } s0 && t0 > s0.T) continue;
                var v = s.Verts; var tr = s.Tris;
                for (int i = 0; i + 2 < tr.Length; i += 3)
                    if (RayTri(ro, rd, v[tr[i]], v[tr[i + 1]], v[tr[i + 2]], out var t))
                    {
                        var hit = new PickHit(s, i / 3, t, ro + rd * t);
                        if (best is not { } bb || t < bb.T) best = hit;
                        if (isPlane) (planeHits ??= new()).Add(hit);
                        else if (solid is not { } ss || t < ss.T) solid = hit;
                    }
            }
            if (best is null) return null;
            planeHits?.Sort((a, b) => a.T.CompareTo(b.T));
            if (best.Shape.Kind == PickKind.Plane)
                return best with { Behind = solid, PlanesBeyond = planeHits?.Where(p => p.T > best.T + 0.5f).ToList() };
            var beyond = planeHits?.Where(p => p.T > best.T).ToList();
            return beyond is { Count: > 0 } ? best with { PlanesBeyond = beyond } : best;
        }

        // fromEye adds the distance to the hit, which is only true for the camera that did the
        // picking — a LOCKED line outlives that camera, so it reports the hit POINT and drops
        // the range rather than displaying a number that quietly goes stale as you fly away.
        string HoverLine(PickHit? hit, bool fromEye = true)
        {
            if (hit is not { } hv) return "";                 // nothing under the cursor: say nothing
            var ws = new Vector3(hv.HitY.X, -hv.HitY.Z, hv.HitY.Y);   // Y-up back to Skyrim
            var s = hv.Shape;
            string tail = fromEye ? $" — hit {ws.X:0},{ws.Y:0},{ws.Z:0}, {hv.T:0} u"
                                  : $" — at {ws.X:0},{ws.Y:0},{ws.Z:0}";
            // The planes the ray crosses past this hit: dashed in the picture, so named here. One
            // clause per plane, nearest first — identity and distance, nothing more.
            string beyond = hv.PlanesBeyond is { Count: > 0 } pb
                ? "  |  " + (s.Kind == PickKind.Plane ? "then " : "planes behind this: ")
                  + string.Join("; ", pb.Select(q => $"PLANE {q.Shape.Plane!.Key} {q.Shape.Plane.KindLabel} {q.Shape.Plane.ProvenanceLabel}"
                                                   + (fromEye ? $" at {q.T:0} u" : "")))
                : "";
            switch (s.Kind)
            {
                case PickKind.Ref:
                    var r = s.Ref;
                    string ed = r.BaseEditorId is { Length: > 0 } e ? e : "(no EditorID)";
                    return $"REFR {r.RefKey} = FormID {opt.RuntimeId(r.RefKey)} — {ed} — {r.MeshRelPath}{(r.BosLabel is { } bl ? "  |  " + bl : "")}{(r.StateLabel is { } stl ? "  |  " + stl : "")}{tail}{beyond}";
                case PickKind.Navmesh:
                    int ti = s.NavTriIds![hv.LocalTri];
                    string red = opt.RedNav.Contains(s.NavKey) ? " [RED — the finding]" : "";
                    return $"NAVM {s.NavKey} = FormID {opt.RuntimeId(s.NavKey)} tri {ti}{red}{tail}{beyond}";
                case PickKind.Plane:
                    var p = s.Plane!;
                    string behind = hv.Behind is { } bh ? $"  |  behind it: {HoverLine(bh with { PlanesBeyond = null }, fromEye)}" : "  |  nothing drawn behind it";
                    return $"PLANE {p.Key} = FormID {p.RuntimeId} — {p.KindLabel} {p.ExtentLabel} — {p.ProvenanceDetail}{tail}{behind}{beyond}";
                default:
                    string desc = opt.TerrainDescribe?.Invoke(ws.X, ws.Y)
                               ?? (s.TerrainTex.Length > 0 ? s.TerrainTex : "(untextured fallback)");
                    return $"terrain — {desc}{tail}{beyond}";
            }
        }

        void PrintDetail(PickHit hv)
        {
            var ws = new Vector3(hv.HitY.X, -hv.HitY.Z, hv.HitY.Y);
            var s = hv.Shape;
            switch (s.Kind)
            {
                case PickKind.Plane:
                    var p = s.Plane!;
                    Console.WriteLine($"   picked PLANE {p.Key} = FormID {p.RuntimeId}  {p.KindLabel} {p.ExtentLabel}  — {p.ProvenanceDetail}");
                    Console.WriteLine($"      at {p.Position.X:0},{p.Position.Y:0},{p.Position.Z:0}  rotation "
                                    + $"{float.RadiansToDegrees(p.Rotation.X):0.#},{float.RadiansToDegrees(p.Rotation.Y):0.#},{float.RadiansToDegrees(p.Rotation.Z):0.#}°"
                                    + $"  bounds {p.Bounds.X:0}x{p.Bounds.Y:0}x{p.Bounds.Z:0}  scale {p.Scale:0.00}  chain {p.ChainLabel}");
                    Console.WriteLine($"      hit at {ws.X:0},{ws.Y:0},{ws.Z:0}, {hv.T:0} u from the eye");
                    if (hv.Behind is { } bh) { Console.WriteLine("      behind it:"); PrintDetail(bh with { PlanesBeyond = null }); }
                    else Console.WriteLine("      nothing drawn behind it on this ray");
                    break;
                case PickKind.Ref:
                    var r = s.Ref;
                    Console.WriteLine($"   picked REFR {r.RefKey} = FormID {opt.RuntimeId(r.RefKey)}"
                                    + $"{(r.Highlight ? "  [highlighted]" : "")}{(r.Marker ? "  [editor marker]" : "")}");
                    Console.WriteLine($"      base {r.BaseKey} = FormID {opt.RuntimeId(r.BaseKey)}"
                                    + $"  {r.BaseEditorId ?? "(no EditorID)"}");
                    if (r.BosLabel is { } bl2) Console.WriteLine($"      {bl2}");
                    if (r.StateLabel is { } stl2) Console.WriteLine($"      {stl2}");
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
            if (hv.PlanesBeyond is { Count: > 0 } pb)
                foreach (var q in pb)
                {
                    var qp = q.Shape.Plane!;
                    Console.WriteLine($"      plane on this ray, {q.T:0} u out: PLANE {qp.Key} = FormID {qp.RuntimeId}  {qp.KindLabel} {qp.ExtentLabel}  — {qp.ProvenanceDetail}");
                }
        }

        // ---- the selection, as three copyable values ---------------------------------------
        // What a pick is FOR is the identity, and what anyone does with an identity is paste it
        // into a query. Reading it off a caption and retyping it is where the digit gets dropped,
        // so the three parts are separate click-to-copy fields on the control bar rather than one
        // sentence: the FormKey, the runtime FormID, and the Data-relative mesh path.
        //
        // Each returns "" when it does not apply, and a field with nothing to say takes itself off
        // the bar — navmesh has an identity but no mesh, terrain has neither.

        // The path the RESOLVER uses, which is the one worth pasting: TexturedScene prepends
        // "meshes" when a record's Model.File omits it, so this must too or the copied path names
        // a file that is not there.
        static string DataRelMesh(string rel)
            => rel.Length == 0 || rel.StartsWith("meshes", StringComparison.OrdinalIgnoreCase)
             ? rel : Path.Combine("meshes", rel);

        string SelFormKey() => locked is not { } l ? "" : l.Shape.Kind switch
        {
            PickKind.Ref => l.Shape.Ref.RefKey.ToString(),
            PickKind.Navmesh => l.Shape.NavKey.ToString(),
            PickKind.Plane => l.Shape.Plane!.Key.ToString(),
            _ => "",
        };
        string SelFormId() => locked is not { } l ? "" : l.Shape.Kind switch
        {
            PickKind.Ref => opt.RuntimeId(l.Shape.Ref.RefKey),
            PickKind.Navmesh => opt.RuntimeId(l.Shape.NavKey),
            PickKind.Plane => l.Shape.Plane!.RuntimeId,
            _ => "",
        };
        string SelPath() => locked is { } l && l.Shape.Kind == PickKind.Ref
                          ? DataRelMesh(l.Shape.Ref.MeshRelPath) : "";
        // What the selection IS, for the row's leading word — so a locked navmesh triangle or a
        // patch of ground is named rather than showing two blank fields.
        string SelWhat() => locked is not { } l ? "" : l.Shape.Kind switch
        {
            PickKind.Ref => "REFR",
            PickKind.Navmesh => $"NAVM tri {l.Shape.NavTriIds![l.LocalTri]}"
                              + (opt.RedNav.Contains(l.Shape.NavKey) ? " [RED]" : ""),
            PickKind.Plane => $"PLANE {l.Shape.Plane!.KindLabel} {l.Shape.Plane!.ProvenanceLabel}",
            _ => "terrain — no record identity to copy",
        };

        void Copy(string what, string value)
        {
            if (value.Length == 0) return;
            // Never silently: a copy that did not happen looks exactly like one that did until
            // the paste comes back empty.
            try { gw.ClipboardString = value; Console.WriteLine($"   copied {what}: {value}"); }
            catch (Exception ex) { Console.WriteLine($"   could not reach the clipboard ({ex.GetType().Name}) — {what} is {value}"); }
        }

        void CopySelectionRow()
        {
            if (locked is null) { Console.WriteLine("   Ctrl+C copies the SELECTION; nothing is selected (LMB an object first)."); return; }
            // One tab-separated ROW, no header — so repeated Ctrl+C into a sheet accumulates a
            // table rather than a header between every line.
            Copy("selection (TSV: FormKey, FormID, mesh)",
                 string.Join("	", new[] { SelFormKey(), SelFormId(), SelPath() }));
        }

        // ---- hover highlight ----------------------------------------------------------------
        // The hovered ref is lifted by the material shader's own EMISSIVE term, which the renderer
        // already feeds per mesh — so the highlight costs five field writes and not one extra draw
        // call. The shader's term is `finalColor += emissiveColor * emissiveMultiple * baseColor`,
        // i.e. it brightens a surface IN PROPORTION TO ITS OWN ALBEDO rather than washing it flat,
        // which is what lets a dark stone and a pale banner both read as picked without either
        // turning into a silhouette.
        //
        // Refs only: navmesh and terrain PickShapes carry no GlMesh (the clip does not touch them
        // either), and they already answer on the caption.
        var glowSaved = new List<(GlMesh Mesh, bool Emissive, bool GlowMap, bool Enabled, Vector3 Colour, float Multiple)>();
        PickShape? glowShape = null;

        void SetGlow(PickShape? s)
        {
            if (ReferenceEquals(s, glowShape)) return;
            foreach (var g in glowSaved)
            {
                g.Mesh.HasEmissive = g.Emissive; g.Mesh.HasGlowMap = g.GlowMap;
                g.Mesh.EmissiveEnabled = g.Enabled; g.Mesh.EmissiveColor = g.Colour;
                g.Mesh.EmissiveMultiple = g.Multiple;
            }
            glowSaved.Clear();
            glowShape = s;
            if (s is null) return;
            // The covering goes with its base, the way the clip already treats them: highlighting
            // a roof but not the snow on it would light half an object.
            foreach (var m in new[] { s.Mesh, s.CoverMesh })
            {
                if (m is null) continue;
                glowSaved.Add((m, m.HasEmissive, m.HasGlowMap, m.EmissiveEnabled, m.EmissiveColor, m.EmissiveMultiple));
                m.HasEmissive = true;
                m.HasGlowMap = false;          // a glow map would mask the lift to the texture's own emissive pattern
                m.EmissiveEnabled = true;
                m.EmissiveColor = new Vector3(0.26f, 0.58f, 1.00f);
                m.EmissiveMultiple = 0.85f;
            }
        }

        // ---- HUD: a caption-style bar at the top of the frame ------------------------------
        // Same visual language as the burned-in PNG caption (dark bar, bold white text), drawn
        // as a textured quad. Rebuilt only when its text or the window width changes.
        //
        // SIZE. 15 px, the burned PNG caption's own size, restored 2026-08-31. It had been
        // doubled to 30 px so a FormID would stay legible while flying — but a FormID is no
        // longer read off this bar: it is a click-to-copy field on the control bar, which is
        // both the reason the size can come back down and the reason it should. The wrap stays
        // (a mesh path is long) so the height is still computed per rebuild rather than fixed.
        const float HudFontPx = 15f, HudLineH = 22f, HudPadX = 12f, HudPadY = 5f;
        // One quad program for all three overlays — the control bar, this caption, and the help
        // card. They differ only in where they sit and which texture they carry.
        var quad = new OverlayQuad();
        var bar = new ControlBar { Report = s => Console.WriteLine("   " + s) };
        var help = new HelpCard();
        bool showHelp = false;                                 // H, and the bar's help button
        // The pose is off by default (2026-08-31). It is a scouting readout, not a flying one:
        // P prints it, F12 burns it into the capture, and the window title carries the cell — so
        // holding a row of the picture open for it all the time was clutter.
        bool showPose = false;
        bool glowOn = true;                                    // hover highlight, the bar's toggle
        int hudTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, hudTex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        Font? hudFont = null;
        foreach (var famName in new[] { "Segoe UI", "Arial", "Verdana" })
            if (SystemFonts.TryGet(famName, out var fam)) { hudFont = fam.CreateFont(HudFontPx, FontStyle.Bold); break; }
        if (hudFont is null)
            foreach (var fam in SystemFonts.Families) { hudFont = fam.CreateFont(HudFontPx, FontStyle.Bold); break; }
        // Line 2 carries only what nothing else does. The nav mode, the route, the clip band, the
        // speed, the sensitivity and the worklist position are all WIDGETS now, so repeating them
        // here was two readouts of one fact. What is left has no other home: the pose (behind its
        // toggle) and the two BUILD-time knives, which no key can undo and which a scene missing
        // its roofs would otherwise read as a finding.
        string StatusLine()
        {
            var parts = new List<string>();
            if (showPose) parts.Add(Pose());
            if (opt.BuildCullAbove is { } bca) parts.Add($"SLICED at build: --cull-above {bca:0} (permanent)");
            if (opt.BuildNavZ is { } bnz) parts.Add($"NAVMESH z {bnz.Z:0} ±{bnz.Tol:0} only (permanent)");
            if (opt.ShowPlanes && opt.PlaneLegend.Length > 0) parts.Add(opt.PlaneLegend);
            if (OcclusionLabel() is { Length: > 0 } ol) parts.Add(ol);
            if (opt.Bos is { BuiltMode: not "off", SwappedCount: > 0 } bv) parts.Add(bv.Summary());
            if (opt.State is { Count: > 0 } sv) parts.Add(sv.Summary());
            return string.Join(" — ", parts);
        }

        string hudLine1 = "";
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
            var wrapped1 = hudLine1.Length > 0 ? WrapLine(hudLine1, avail) : new List<string>();
            var wrapped2 = hudLine2.Length > 0 ? WrapLine(hudLine2, avail) : new List<string>();
            hudBarH = wrapped1.Count + wrapped2.Count == 0
                    ? 0 : (int)(HudPadY * 2 + HudLineH * (wrapped1.Count + wrapped2.Count));
            if (hudBarH == 0) return;                  // nothing to say: no band at all
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
        // Every overlay, in one pass: the control bar at the top, the caption directly under it,
        // and the help card centred over the picture. The caption is no longer pinned to y=0 —
        // it hangs off the bar's measured height, so a bar that wrapped onto an extra row on a
        // narrow window pushes the caption down instead of drawing underneath it.
        void DrawOverlays(int fbW, int fbH)
        {
            if (fbW <= 0 || fbH <= 0) return;
            if (opt.ReportMode) return;
            bar.Sync(fbW);
            RebuildHud(fbW);
            if (showHelp) help.Sync(HelpLines(), fbW, fbH);
            quad.Begin();
            quad.DrawPixels(bar.Texture, 0, 0, bar.Width, bar.Height, fbW, fbH);
            if (hudFont is not null) quad.DrawPixels(hudTex, 0, bar.Height, fbW, hudBarH, fbW, fbH);
            if (showHelp)
                quad.DrawPixels(help.Texture, (fbW - help.Width) / 2f,
                                MathF.Max(bar.Height + hudBarH + 16f, (fbH - help.Height) / 2f),
                                help.Width, help.Height, fbW, fbH);
            quad.End();
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
                outPath, reportMode: opt.ReportMode);
            Console.WriteLine($"   wrote {outPath}");
            Console.WriteLine($"   replay: {PoseArgs()}");
        }

        // The help, once, as data — so the console copy and the in-window card cannot drift apart.
        // They used to be one console print, which is exactly why H "did nothing": the key fired,
        // the answer just landed in a window nobody flying is looking at.
        List<(string Head, string Body)> HelpLines()
        {
            var h = new List<(string, string)>
            {
                ("fly", "WASD move (W/S along the view direction), E/Space up, Q/C down, hold RMB and move the mouse to look. Shift x5, Ctrl x0.2 on every rate, movement and clip alike."),
                ("bar", "every control below is also a widget on the top bar. Click a number box and type (Tab moves on, Enter commits, Esc cancels), or drag its slider. The bar and the keys drive the same state, so either moves both."),
                ("feel", $"scroll = fly speed (now {speed:0} u/s); , and . = look sensitivity (now {lookSens:0.000} deg/px). Both echo a paste-ready flag to the console."),
                ("pick", "hover identifies whatever is under the cursor on the caption line; LMB LOCKS that readout and prints the full detail; LMB again unlocks. The cursor is free whenever RMB is up."),
                ("nav", $"N cycles the navmesh display off -> filled -> wire -> both{(hasNav ? $" (now {navMode})" : " — this run carries none")}."),
                ("clip", $"[ and ] move the CEILING, ; and ' move the FLOOR (each starts at the camera's z on first touch), \\ turns both off (now {ClipLabel()})."),
                ("route", "R shows/hides the walking route" + (opt.RouteCount > 0
                    ? $" ({opt.RouteCount} waypoint(s), now {(opt.ShowRoute ? "shown" : "hidden")})."
                    : " — this run carries none.")),
                ("planes", "L shows/hides the occlusion planes" + (opt.PlaneCount > 0
                    ? $" ({opt.PlaneCount} in this cell, now {(opt.ShowPlanes ? "shown" : "hidden")}) — grey vanilla, gold inherited, magenta added, teal moved; hover one for its identity and what stands behind it."
                    : " — this cell carries none.")),
                ("occlusion", "O cycles the occlusion preview off -> on -> diff" + (hasOccl
                    ? $" (now {occlMode}): on leaves out every ref the planes would cull from the camera, diff draws everything and tints those refs red. Re-evaluated as you fly."
                    : " — this cell carries no planes to preview.")),
                ("bos", "B toggles the Base Object Swapper layer's paint on <-> diff" + (opt.Bos is { BuiltMode: not "off" } bh
                    ? $" ({bh.SwappedCount} drawn ref(s) carry the base the game swaps in; now {bosMode}): diff tints them teal; hover one for record base -> effective base and the ini line."
                    : " — this scene was built with --bos off (record bases).")),
                ("state", "T toggles the world state's paint on <-> diff" + (opt.State is { } sh
                    ? $" (STATE {sh.StateName}: {sh.Arrived.Count} drawn ref(s) arrive, {sh.Departed.Count} depart; now {stateMode}): diff tints arrivals green and draws departures red; hover one for the switch it follows."
                    : " — this scene was built without --state.")),
            };
            if (wl is not null)
                h.Add(("shots", $"PageUp/PageDown step this cell's shots (now {shotIdx + 1}/{Math.Max(shots.Count, 1)}); "
                              + $"Ctrl+PageUp/PageDown step the worklist's cells (now {opt.WorklistCell + 1}/{wl.Cells.Count})"
                              + (opt.CanSwitchCell ? "." : " — cell switching is off in this run.")));
            h.Add(("out", $"P prints --eye/--yaw/--pitch, F12 saves a captioned PNG (no overlays and no highlight in it), H or F1 shows this card, Esc closes the card or quits. The bar's 'pose' button keeps the camera numbers on screen (now {(showPose ? "on" : "off")}); 'glow' turns the hover highlight off (now {(glowOn ? "on" : "off")})."));
            return h;
        }

        void PrintHelp()
        {
            foreach (var (head, body) in HelpLines()) Console.WriteLine($"   {head,-7} {body}");
        }

        // ---- the control bar: the same controls, drawn --------------------------------------
        // Each widget is a pair of delegates onto the loop's own state, never a copy of it — so a
        // keypress moves the widget and a drag does exactly what the key would have done. There
        // is no second implementation to keep in step, which is the only version of this that
        // stays honest.
        //
        // Anything the run cannot do — no navmesh, no route, one cell — is drawn GREYED, carrying
        // the sentence the key already printed, rather than hidden. A control that vanishes and a
        // scene that is missing something look alike from the outside, and only one of them is a
        // finding.
        void SetNav(string mode)
        {
            navMode = mode; ApplyNav(); pickDirty = true;
            Console.WriteLine($"   nav: {navMode}" + (navMode == "off" ? "  (the finding's outline goes with it)" : ""));
        }
        void SetRoute(bool on)
        {
            opt.ShowRoute = on;
            Console.WriteLine($"   route: {(on ? "shown" : "hidden")}  ({opt.RouteCount} waypoint(s)"
                            + (opt.RouteLabel.Length > 0 ? $" — {opt.RouteLabel}" : "") + ")");
        }
        // The planes layer, the same shape as the route: a run with nothing to show says so.
        void SetPlanes(bool on)
        {
            if (opt.PlaneCount == 0)
            { Console.WriteLine("   planes: this cell carries no occlusion planes — nothing to show"); return; }
            opt.ShowPlanes = on; pickDirty = true;
            Console.WriteLine($"   planes: {(on ? "shown" : "hidden")}  ({opt.PlaneCount} plane(s))");
        }
        // The Base Object Swapper layer's paint: on <-> diff live (one scene, two paints); a scene built with
        // --bos off cannot show swaps and says so rather than pretending to. (bosMode is declared with occlMode.)
        void SetBos(string mode)
        {
            if (opt.Bos is null || opt.Bos.BuiltMode == "off")
            { Console.WriteLine("   bos: this scene was built without the Base Object Swapper layer — rerun with --bos on or --bos diff"); return; }
            if (opt.Bos.SwappedCount == 0) { Console.WriteLine("   bos: no drawn ref in this cell carries a swapped base — nothing to paint"); return; }
            if (!opt.Bos.Apply(mode)) return;
            bosMode = mode;
            Console.WriteLine("   " + opt.Bos.Summary());
        }
        // The world state's paint: on <-> diff live when the scene was built diff (the departed refs are resident);
        // a scene built on holds no departed ref and says so.
        void SetState(string mode)
        {
            if (opt.State is null) { Console.WriteLine("   state: this scene was built without a world state — rerun with --state <switch>"); return; }
            if (opt.State.Count == 0) { Console.WriteLine("   state: no drawn ref in this cell changes with the flip — nothing to paint"); return; }
            if (mode == "diff" && opt.State.BuiltMode == "on") { Console.WriteLine("   state: this scene was built with --state-view on — the departed refs are not resident; rerun with --state-view diff"); return; }
            if (!opt.State.Apply(mode)) return;
            stateMode = mode;
            ApplyClip(); SetGlow(null); pickDirty = true;
            Console.WriteLine("   " + opt.State.Summary());
        }
        // The two ends stay ordered however they are set — the keys already refused to push one
        // past the other, and a typed 4000 into the floor box must not quietly invert the band.
        void SetCeiling(float? z)
        {
            clipHi = z is null ? null : MathF.Max(z.Value, clipLo ?? float.NegativeInfinity);
            ApplyClip(); pickDirty = true;
        }
        void SetFloor(float? z)
        {
            clipLo = z is null ? null : MathF.Min(z.Value, clipHi ?? float.PositiveInfinity);
            ApplyClip(); pickDirty = true;
        }
        // One implementation for PageUp/PageDown and for the bar's stepper, boundary message
        // included, so clicking the arrow at the last shot says what pressing the key says.
        void StepShot(int dir)
        {
            if (wl is null) { Console.WriteLine("   PageUp/PageDown step a worklist; this run was not given --worklist."); return; }
            if (shots.Count == 0) { Console.WriteLine("   worklist: this cell has no shots."); return; }
            if (shotIdx + dir < 0 || shotIdx + dir >= shots.Count)
            {
                Console.WriteLine($"   worklist: {(dir < 0 ? "first" : "last")} shot of this cell"
                                + (opt.CanSwitchCell ? " — Ctrl+PageUp/PageDown for the previous/next cell" : ""));
                return;
            }
            ApplyShot(shotIdx + dir, announce: true);
        }
        void ToggleHelp()
        {
            showHelp = !showHelp;
            if (showHelp) PrintHelp();          // the console copy stays: that is where a value gets copied FROM
        }

        // The clip tracks span the SCENE's own static z-extent, padded — a slider is only worth
        // dragging if its ends are somewhere a roof actually is. A run with nothing clippable
        // still gets a usable range instead of a zero-width one.
        float clipZLo = -4096f, clipZHi = 4096f;
        if (clippable.Length > 0)
        {
            clipZLo = clippable.Min(s => s.Min.Y);
            clipZHi = clippable.Max(s => s.Max.Y);
            float zPad = MathF.Max((clipZHi - clipZLo) * 0.05f, 64f);
            clipZLo -= zPad; clipZHi += zPad;
        }

        bar.Add(new UiButton
        {
            Pinned = true,
            Text = () => bar.Collapsed ? "▼ controls" : "▲ controls",
            OnClick = () => bar.Collapsed = !bar.Collapsed,
        });
        bar.Add(new UiLabel
        {
            Pinned = true, Bright = true,
            Hidden = () => !bar.Collapsed,        // the window title already says it; expanded, the row wants the width
            Text = () => opt.CellLabel,
        });
        bar.Add(new UiSegment
        {
            Label = "navmesh (N)",
            Options = new[] { "off", "filled", "wire", "both" },
            Get = () => navMode,
            Set = SetNav,
            DisabledWhy = () => hasNav ? ""
                : "nav: this run carries no navmesh (--nav off, or none in the cell) — nothing to switch",
        });
        bar.Add(new UiButton
        {
            Text = () => opt.RouteCount == 0 ? "route (R)" : opt.ShowRoute ? "route shown (R)" : "route hidden (R)",
            IsOn = () => opt.RouteCount > 0 && opt.ShowRoute,
            OnClick = () => SetRoute(!opt.ShowRoute),
            DisabledWhy = () => opt.RouteCount > 0 ? ""
                : "route: this run carries no route (--route X,Y, or a worklist shot's route) — nothing to toggle",
        });
        // Which walk this is. A waypoint count with no sentence attached says nothing about which
        // path is on screen when a worklist holds several, so the label travels with the count.
        bar.Add(new UiLabel
        {
            Hidden = () => opt.RouteCount == 0,
            Text = () => $"{opt.RouteCount} wpt" + (opt.RouteLabel.Length > 0 ? $" - {opt.RouteLabel}" : ""),
        });
        bar.Add(new UiButton
        {
            Text = () => opt.PlaneCount == 0 ? "planes (L)" : opt.ShowPlanes ? "planes shown (L)" : "planes hidden (L)",
            IsOn = () => opt.PlaneCount > 0 && opt.ShowPlanes,
            OnClick = () => SetPlanes(!opt.ShowPlanes),
            DisabledWhy = () => opt.PlaneCount > 0 ? ""
                : "planes: this cell carries no occlusion planes — nothing to toggle",
        });
        bar.Add(new UiSegment
        {
            Label = "occlusion (O)",
            Options = new[] { "off", "on", "diff" },
            Get = () => occlMode,
            Set = SetOcclusion,
            DisabledWhy = () => hasOccl ? "" : "occlusion: this cell carries no occlusion planes — nothing to preview",
        });
        bar.Add(new UiSegment
        {
            Label = "bos (B)",
            Options = new[] { "on", "diff" },
            Get = () => bosMode,
            Set = SetBos,
            DisabledWhy = () => opt.Bos is null || opt.Bos.BuiltMode == "off"
                ? "bos: this scene was built without the Base Object Swapper layer (--bos on|diff) — nothing to paint"
                : opt.Bos.SwappedCount == 0 ? "bos: no drawn ref in this cell carries a swapped base — nothing to paint" : "",
        });
        bar.Add(new UiSegment
        {
            Label = "state (T)",
            Options = new[] { "on", "diff" },
            Get = () => stateMode,
            Set = SetState,
            DisabledWhy = () => opt.State is null ? "state: this scene was built without a world state (--state <switch>) — nothing to paint"
                : opt.State.Count == 0 ? "state: no drawn ref in this cell changes with the flip — nothing to paint"
                : opt.State.BuiltMode == "on" ? "state: built with --state-view on — the departed refs are not resident (rerun with --state-view diff to toggle)" : "",
        });

        bar.Row(new UiNumber
        {
            Label = "fly speed", Unit = "u/s", Min = 20f, Max = 20000f, Log = true, Format = "0",
            Get = () => speed,
            Set = v => speed = Math.Clamp(v ?? speed, 20f, 20000f),
            Echo = _ => $"fly speed: {speed:0} u/s  (--fly-speed {speed:0})",
        });
        bar.Add(new UiNumber
        {
            Label = "look (, .)", Unit = "°/px", Min = 0.01f, Max = 2f, Log = true, Format = "0.###",
            Get = () => lookSens,
            Set = v => lookSens = Math.Clamp(v ?? lookSens, 0.01f, 2f),
            Echo = _ => $"look sensitivity: {lookSens:0.000} deg/px  (--look-sens {lookSens:0.###})",
        });

        bar.Row(new UiNumber
        {
            Label = "clip ceiling ([ ])", Unit = "z", Min = clipZLo, Max = clipZHi, Format = "0", Nullable = true,
            ScrollStep = 64f, Seed = () => eye.Z,
            Get = () => clipHi, Set = SetCeiling, Echo = _ => { PrintClip(); return ""; },
        });
        bar.Add(new UiButton { Text = () => "at eye", OnClick = () => { SetCeiling(eye.Z); PrintClip(); } });
        bar.Add(new UiButton { Text = () => "clear", OnClick = () => { SetCeiling(null); PrintClip(); } });

        bar.Add(new UiNumber
        {
            Label = "clip floor (; ')", Unit = "z", Min = clipZLo, Max = clipZHi, Format = "0", Nullable = true,
            ScrollStep = 64f, Seed = () => eye.Z,
            Get = () => clipLo, Set = SetFloor, Echo = _ => { PrintClip(); return ""; },
        });
        bar.Add(new UiButton { Text = () => "at eye", OnClick = () => { SetFloor(eye.Z); PrintClip(); } });
        bar.Add(new UiButton { Text = () => "clear", OnClick = () => { SetFloor(null); PrintClip(); } });
        bar.Add(new UiButton
        {
            Text = () => "both off (\\)",
            OnClick = () => { clipLo = clipHi = null; ApplyClip(); pickDirty = true; PrintClip(); },
        });
        // The one thing about the clip that the two number boxes do NOT say. It used to ride on
        // the caption next to a repeat of the band; here it sits with the controls that caused it.
        bar.Add(new UiLabel
        {
            Hidden = () => clipLo is null && clipHi is null,
            Text = () => $"{clipHidden:N0} of {clippable.Length:N0} hidden",
        });

        if (wl is not null)
        {
            bar.Row(new UiStepper
            {
                Text = () => $"cell {opt.WorklistCell + 1}/{wl.Cells.Count} (Ctrl+PgUp/PgDn)",
                Step = RequestCell,
                DisabledWhy = () => opt.CanSwitchCell ? ""
                    : "worklist: this run holds one cell only — cell switching needs the whole worklist, not a single --worklist cell.",
            });
            bar.Add(new UiStepper
            {
                Text = () => shots.Count > 0 ? $"shot {shotIdx + 1}/{shots.Count} (PgUp/PgDn)" : "shots",
                Step = StepShot,
                DisabledWhy = () => shots.Count > 0 ? "" : "worklist: this cell has no shots.",
            });
            bar.Add(new UiLabel { Text = () => shots.Count > 0 ? shots[shotIdx].Label : "" });
        }

        bar.Add(new UiButton
        {
            Text = () => "print pose (P)",
            OnClick = () => { Console.WriteLine($"   pose: {Pose()}"); Console.WriteLine($"   args: {PoseArgs()}"); },
        });
        bar.Add(new UiButton { Text = () => "save PNG (F12)", OnClick = () => wantShot = true });
        bar.Add(new UiButton
        {
            Text = () => "pose", IsOn = () => showPose,
            OnClick = () => { showPose = !showPose; if (showPose) Console.WriteLine($"   pose: {Pose()}"); },
        });
        bar.Add(new UiButton
        {
            Text = () => "glow", IsOn = () => glowOn,
            OnClick = () => { glowOn = !glowOn; if (!glowOn) SetGlow(null); },
        });
        bar.Add(new UiButton { Text = () => "help (H)", IsOn = () => showHelp, OnClick = ToggleHelp });
        bar.Add(new UiButton { Text = () => "quit (Esc)", OnClick = gw.Close });

        // ---- the selection row: three values, each of which copies itself ------------------
        // The whole row takes itself off the bar when nothing is selected — it is a readout, and a
        // readout with nothing to report should not hold a row of the picture open.
        Func<bool> noSel = () => locked is null;
        bar.Row(new UiLabel
        {
            Hidden = noSel, Bright = true,
            Text = () => "selected: " + SelWhat() + (locked is { } l && !Drawn(l.Shape) ? "  [not drawn now]" : ""),
        });
        bar.Add(new UiCopyField
        {
            Hidden = () => noSel() || SelFormKey().Length == 0,
            Label = "FormKey", Value = SelFormKey, Copy = v => Copy("FormKey", v), KeepHead = true, MaxW = 380f,
        });
        bar.Add(new UiCopyField
        {
            Hidden = () => noSel() || SelFormId().Length == 0,
            Label = "FormID", Value = SelFormId, Copy = v => Copy("FormID", v),
        });
        bar.Add(new UiCopyField
        {
            Hidden = () => noSel() || SelPath().Length == 0,
            Label = "mesh", Value = SelPath, Copy = v => Copy("mesh path", v), MaxW = 480f,
        });
        bar.Add(new UiButton
        {
            Hidden = noSel, Text = () => "clear",
            OnClick = () =>
            {
                locked = null; hover = null; SetGlow(null);
                lastPickPos = new OpenTK.Mathematics.Vector2(-1000f, -1000f);
                hudLine1 = "";
                Console.WriteLine("   unlocked");
            },
        });

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
        if (opt.PlaneCount > 0)
            Console.WriteLine($"   planes: {opt.PlaneCount} occlusion plane(s) resident, layer {(opt.ShowPlanes ? "shown" : "hidden")}; L toggles it; "
                            + $"O cycles the occlusion preview (entry: {occlMode})");
            Console.WriteLine($"   bos: {(opt.Bos is { BuiltMode: not "off" } bv ? $"{bv.SwappedCount} drawn ref(s) with a swapped base; B toggles the paint on <-> diff (entry: {bosMode})" : "built without the layer (--bos on|diff)")}");
            Console.WriteLine($"   state: {(opt.State is { } sv ? $"STATE {sv.StateName} — {sv.Arrived.Count} arrive, {sv.Departed.Count} depart; T toggles the paint on <-> diff (entry: {stateMode})" : "built without --state")}");
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
        // VP_FLY_PLANES=on|off: the L key's effect, headlessly.
        if (Environment.GetEnvironmentVariable("VP_FLY_PLANES") is { Length: > 0 } planesSpec)
        {
            string want = planesSpec.ToLowerInvariant();
            if (want is not ("on" or "off"))
                Console.WriteLine($"   [auto] VP_FLY_PLANES '{want}' is not on|off — ignored");
            else if (opt.PlaneCount == 0)
                Console.WriteLine($"   [auto] VP_FLY_PLANES={want} ignored: this cell carries no occlusion planes");
            else { opt.ShowPlanes = want == "on"; Console.WriteLine($"   [auto] VP_FLY_PLANES={want}"); }
        }
        // VP_FLY_OCCLUSION=off|on|diff: the O key's effect, headlessly.
        if (Environment.GetEnvironmentVariable("VP_FLY_OCCLUSION") is { Length: > 0 } occlSpec)
        {
            string want = occlSpec.ToLowerInvariant();
            if (want is not ("off" or "on" or "diff"))
                Console.WriteLine($"   [auto] VP_FLY_OCCLUSION '{want}' is not off|on|diff — ignored");
            else if (!hasOccl)
                Console.WriteLine($"   [auto] VP_FLY_OCCLUSION={want} ignored: this cell carries no occlusion planes");
            else { occlMode = want; Console.WriteLine($"   [auto] VP_FLY_OCCLUSION={want}"); }
        }
        // The preview's first evaluation, at the entry eye — before the first frame, so a headless
        // capture and a hand-flown window open on the same picture.
        ApplyOcclusion(force: true);
        if (hasOccl && occlMode != "off") Console.WriteLine("   " + opt.Occlusion!.Summary(occlMode));
        // The two pieces of state the control bar added, each with the headless knob every other
        // live control in this loop already has — a control no test can reach is a control nothing
        // checks. Both are only VISIBLE in a VP_FLY_HUD=1 capture, which is the point: they are
        // the overlay, and F12 excludes the overlay.
        if (Environment.GetEnvironmentVariable("VP_FLY_HELP") == "1") { showHelp = true; PrintHelp(); }
        if (Environment.GetEnvironmentVariable("VP_FLY_POSE") == "1") { showPose = true; }
        if (Environment.GetEnvironmentVariable("VP_FLY_GLOW") is { Length: > 0 } glowSpec)
        {
            string want = glowSpec.ToLowerInvariant();
            if (want is not ("on" or "off")) Console.WriteLine($"   [auto] VP_FLY_GLOW '{want}' is not on|off — ignored");
            else { glowOn = want == "on"; Console.WriteLine($"   [auto] hover highlight: {want}"); }
        }
        if (Environment.GetEnvironmentVariable("VP_FLY_BAR") is { Length: > 0 } barSpec)
        {
            string want = barSpec.ToLowerInvariant();
            if (want is not ("expanded" or "collapsed"))
                Console.WriteLine($"   [auto] VP_FLY_BAR '{want}' is not expanded|collapsed — ignored");
            else { bar.Collapsed = want == "collapsed"; Console.WriteLine($"   [auto] control bar: {want}"); }
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

        // VP_FLY_FRAMES is a TEST, and a test must not read the machine's live keyboard and
        // mouse. It did, and the window takes focus when it opens, so two identical smoke runs
        // captured DIFFERENT frames — measured 2026-08-28: one drifted 138 u in x and 218 u in
        // z before frame 30, because whatever was being typed at the desk went into the window.
        // Under VP_FLY_FRAMES the loop is therefore input-deaf: the camera stays exactly where
        // the command line put it, and a capture is a function of the arguments alone.
        bool autoRun = autoFrames > 0;

        // Text for a focused number box. Reading the window's own TextInput is what makes the
        // keypad, a shifted hyphen and a non-US layout all deliver the character the person
        // actually pressed; Type() ignores everything when no box has focus.
        gw.TextInput += ev => bar.Type(ev.AsString);

        gw.UpdateFrame += e =>
        {
            var kb = gw.KeyboardState;
            var ms = gw.MouseState;
            float dt = (float)e.Time;

            if (autoRun)
            {
                SyncCamera();
                ApplyOcclusion();
                hudLine2 = StatusLine();
                return;
            }

            bool looking = ms.IsButtonDown(MouseButton.Right);
            gw.CursorState = looking ? CursorState.Grabbed : CursorState.Normal;
            if (looking)
            {
                yaw = (yaw + ms.Delta.X * lookSens + 360f) % 360f;
                pitch = Math.Clamp(pitch + ms.Delta.Y * lookSens, -89f, 89f);
            }

            // ---- the control bar gets the mouse first ---------------------------------------
            // Laid out here as well as in the draw, because a rect that does not exist yet cannot
            // be clicked: without this the bar would be dead for its first frame. Layout is also
            // what a hit test reads, so the two must not be a frame apart.
            //
            // The bar is measured in FRAMEBUFFER pixels and the cursor arrives in CLIENT ones.
            // They are the same number on this platform today, but scaling by the ratio costs one
            // multiply and means a DPI-scaled window clicks where it looks.
            int barFbW = Math.Max(gw.FramebufferSize.X, 1);
            bar.Layout(barFbW);
            float uiScale = barFbW / (float)Math.Max(gw.ClientSize.X, 1);
            var mpc = gw.MousePosition;
            // While looking, the cursor is grabbed and its position runs away; parking the bar's
            // idea of it off-screen stops a highlight lighting up under a cursor that is not there.
            bar.Mouse = looking ? (-1e6f, -1e6f) : (mpc.X * uiScale, mpc.Y * uiScale);
            // A slider grab ends when the button is no longer down — NOT on the released EDGE.
            // The edge can be missed (RMB taken over mid-drag and the cursor grabbed away, the
            // window losing focus with the button held), and a grab that outlives its drag leaves
            // the bar holding the mouse forever, with picking dead and no way back.
            bool uiHasMouse = !looking && (bar.Contains(bar.Mouse.X, bar.Mouse.Y) || bar.Dragging);
            if (looking || !ms.IsButtonDown(MouseButton.Left)) bar.Release();
            else if (bar.Dragging) { uiHasMouse = true; bar.Drag(bar.Mouse.X, bar.Mouse.Y); }
            if (!looking && ms.IsButtonPressed(MouseButton.Left))
                uiHasMouse |= bar.Press(bar.Mouse.X, bar.Mouse.Y);

            // A focused number box owns the keyboard. Every flight and hotkey read below goes
            // through these three, so typing 12 into the ceiling box cannot also fly the camera
            // forward or cycle the navmesh — one gate instead of a condition per key, which is
            // the version that stays right when a key is added later.
            bool typing = bar.IsEditing;
            bool KDown(Keys k) => !typing && kb.IsKeyDown(k);
            bool KHit(Keys k) => !typing && kb.IsKeyPressed(k);
            bool KUp(Keys k) => !typing && kb.IsKeyReleased(k);
            if (typing)
            {
                if (kb.IsKeyPressed(Keys.Backspace)) bar.Backspace();
                if (kb.IsKeyPressed(Keys.Enter) || kb.IsKeyPressed(Keys.KeyPadEnter)) bar.CommitEdit();
                if (kb.IsKeyPressed(Keys.Tab)) bar.FocusNext();
                if (kb.IsKeyPressed(Keys.Escape)) bar.CancelEdit();
            }

            float mult = KDown(Keys.LeftShift) || KDown(Keys.RightShift) ? 5f
                       : KDown(Keys.LeftControl) || KDown(Keys.RightControl) ? 0.2f : 1f;
            float step = speed * mult * dt;
            var fwd = Forward();
            float yr = float.DegreesToRadians(yaw);
            var right = new Vector3(MathF.Cos(yr), -MathF.Sin(yr), 0f);
            if (KDown(Keys.W)) eye += fwd * step;
            if (KDown(Keys.S)) eye -= fwd * step;
            if (KDown(Keys.A)) eye -= right * step;
            if (KDown(Keys.D)) eye += right * step;
            bool ctrlHeld = KDown(Keys.LeftControl) || KDown(Keys.RightControl);
            if (KDown(Keys.E) || KDown(Keys.Space)) eye.Z += step;
            // Ctrl+C is the copy gesture, and Ctrl is also the slow modifier — so C gives up its
            // Ctrl case rather than sinking the camera every time a value is copied. Q still
            // descends with Ctrl held, at the same slow rate, so nothing is actually lost.
            if (KDown(Keys.Q) || (KDown(Keys.C) && !ctrlHeld)) eye.Z -= step;
            if (ctrlHeld && KHit(Keys.C)) CopySelectionRow();
            // The wheel belongs to whatever it is resting on. Over the bar that is the widget
            // under it (or nothing, if the panel is bare there); anywhere else it is the fly
            // speed it has always been.
            bool uiTookWheel = !looking && bar.Scroll(bar.Mouse.X, bar.Mouse.Y, ms.ScrollDelta.Y);
            if (!uiTookWheel && MathF.Abs(ms.ScrollDelta.Y) > 0f)
                speed = Math.Clamp(speed * MathF.Pow(1.25f, ms.ScrollDelta.Y), 20f, 20000f);
            // Look sensitivity, on the same 1.25x ladder the scroll uses for speed. Printed on
            // change: the number that finally feels right is worth carrying to --look-sens.
            if (KHit(Keys.Comma) || KHit(Keys.Period))
            {
                lookSens = Math.Clamp(lookSens * (KHit(Keys.Period) ? 1.25f : 1f / 1.25f), 0.01f, 2f);
                Console.WriteLine($"   look sensitivity: {lookSens:0.000} deg/px  (--look-sens {lookSens:0.###})");
            }

            // ---- navmesh display ------------------------------------------------------------
            if (KHit(Keys.N))
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
            if (KHit(Keys.R))
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

            // ---- the occlusion planes, and their preview -------------------------------------
            if (KHit(Keys.L)) SetPlanes(!opt.ShowPlanes);
            if (KHit(Keys.O)) SetOcclusion(occlMode switch { "off" => "on", "on" => "diff", _ => "off" });
            if (KHit(Keys.B)) SetBos(bosMode == "diff" ? "on" : "diff");
            if (KHit(Keys.T)) SetState(stateMode == "diff" ? "on" : "diff");

            // ---- height clip ----------------------------------------------------------------
            // Held keys, not taps: sweeping a ceiling through a two-storey interior is hundreds
            // of units, and one tap per 4 u would be unusable. The first frame a key is down
            // seeds that end at the camera's own z (?? = "not set yet"), so the gesture is
            // "stand on the floor you want and hold [".
            bool ceilDown = KDown(Keys.LeftBracket), ceilUp = KDown(Keys.RightBracket);
            bool floorDown = KDown(Keys.Semicolon), floorUp = KDown(Keys.Apostrophe);
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
            if (KHit(Keys.Backslash)) { clipLo = clipHi = null; clipDirty = true; PrintClip(); }
            if (clipDirty) { ApplyClip(); pickDirty = true; }
            // On release, echo the band — the console is where a number gets copied from, and
            // --cull-above is what the static deliverable needs.
            if (KUp(Keys.LeftBracket) || KUp(Keys.RightBracket)
             || KUp(Keys.Semicolon) || KUp(Keys.Apostrophe)) PrintClip();

            // Worklist paging. Ctrl is the cell modifier, so the two live one keypress apart:
            // PageDown walks the shots of this cell, Ctrl+PageDown walks to the next cell.
            if (KHit(Keys.PageUp) || KHit(Keys.PageDown))
            {
                int dir = KHit(Keys.PageDown) ? 1 : -1;
                bool ctrl = ctrlHeld;
                if (wl is null) Console.WriteLine("   PageUp/PageDown step a worklist; this run was not given --worklist.");
                else if (ctrl) RequestCell(dir);
                else StepShot(dir);
            }

            if (KHit(Keys.P))
            {
                Console.WriteLine($"   pose: {Pose()}");
                Console.WriteLine($"   args: {PoseArgs()}");
            }
            if (KHit(Keys.F12)) wantShot = true;
            // H used to print to the console and nothing else, which from inside the window was
            // indistinguishable from a dead key. It now raises the card AND prints, because the
            // console is still where a number gets copied from.
            if (KHit(Keys.H) || KHit(Keys.F1)) ToggleHelp();
            // Esc dismisses the card first. Quitting a flight by reflex, while reading the help
            // that told you Esc quits, loses the pose you flew to.
            if (KHit(Keys.Escape)) { if (showHelp) showHelp = false; else gw.Close(); }

            SyncCamera();
            ApplyOcclusion();                      // a no-op until the eye has moved

            // ---- hover, and the LOCK ---------------------------------------------------------
            // Locked, the HUD keeps showing ONE object's identity while the camera moves — which
            // is the point: reading a FormKey off a wall you are also trying to fly around meant
            // holding the cursor perfectly still. So picking is suspended while locked (the
            // readout cannot drift, and the raycast cannot cost anything), and the next LMB
            // anywhere releases it, hit or miss.
            // A cursor over the bar is not over the scene. Picking is left FROZEN rather than
            // reset while it is up there: the last answer stays on the caption, so reaching for a
            // widget does not wipe the identity you were reading. Locking is suspended too — a
            // click meant for a button must not also pin whatever the panel is drawn over.
            if (!looking && !uiHasMouse)
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
                        hudLine1 = "";                 // the selection row on the bar is the readout now
                        PrintDetail(hv);
                    }
                }
                else if (ms.IsButtonPressed(MouseButton.Left))
                {
                    locked = null; hover = null;
                    lastPickPos = new OpenTK.Mathematics.Vector2(-1000f, -1000f);   // force the next hover to re-pick
                    hudLine1 = "";
                    Console.WriteLine("   unlocked");
                }
            }

            // The hover highlight follows the CURSOR, not the lock: it answers "what would I get
            // if I clicked here". While locked, hovering is suspended and so is the glow, which is
            // right — the locked object is named on the bar and does not need lighting up.
            SetGlow(glowOn && locked is null && !looking && !uiHasMouse ? hover?.Shape : null);

            if (hudLine2.Length == 0 || titleClock.ElapsedMilliseconds > 150)
            {
                titleClock.Restart();
                hudLine2 = StatusLine();
            }
        };

        gw.RenderFrame += e =>
        {
            int w = gw.FramebufferSize.X, h = gw.FramebufferSize.Y;
            if (w <= 0 || h <= 0) return;                        // minimized
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            // The hover highlight is a cursor artefact, not the scene. It comes off BEFORE the
            // render the capture reads, so an F12 frame is the same pixels a static run of the
            // same pose would produce; the next hover puts it straight back. VP_FLY_HUD=1 keeps
            // it, on the same reasoning that keeps the bar: that capture is a picture OF the
            // tool's chrome, and the highlight is chrome.
            if (wantShot && !hudInShot) SetGlow(null);
            renderer.Render(camera, w, h);
            var view = camera.GetViewMatrix();
            var proj = camera.GetProjectionMatrix((float)w / h);
            overlayDraw?.Invoke(view, proj);
            // F12 grabs the frame WITHOUT any overlay — no control bar, no caption, no help
            // card. The PNG carries the standard burned caption instead, so a capture looks like
            // a render and not like a screenshot of a tool. VP_FLY_HUD=1 moves the grab to after
            // the overlays, which is the only way a headless test can see them at all.
            if (wantShot && !hudInShot) { wantShot = false; CaptureShot(w, h); if (autoFrames > 0) AutoFinish(); }
            DrawOverlays(w, h);
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
                    if (glowOn) SetGlow(hit?.Shape);
                    if (hit is { } hv)
                    {
                        PrintDetail(hv);
                        // VP_FLY_LOCK: the same state LMB produces, so the locked HUD line is
                        // what a VP_FLY_HUD capture shows.
                        if (autoLock)
                        {
                            locked = hv;
                            hudLine1 = "";
                            Console.WriteLine("   [auto] locked");
                        }
                    }
                }
                wantShot = true;
            }
        };

        gw.Run();

        SetGlow(null);                      // hand every mesh back its own emissive material
        GL.DeleteTexture(hudTex);
        bar.Dispose();
        help.Dispose();
        quad.Dispose();
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
}
