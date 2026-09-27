using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace LeftBehind;

// THE SELF-TEST — synthetic fixtures only, no game data, no archives, no load order on disk.
//
// Each fixture writes a tiny three-plugin order through the SAME ingestion path the real run uses
// (OverlapIndex.Record, then EnableArms for the load-time chain): plugin 0 places the base world, plugin 1 places the
// panel (the occlusion plugin), plugin 2 is the city plugin. The base arm is fed plugins 0-1, the city arm 0-2. The
// geometry is box soups (ContactIndex.BoxSoup), handed to each arm through the same per-base provider the real run
// fills from meshes. Then LeftBehindRule.Run classifies every panel of plugin 1, exactly as the real run does, and the
// case asserts the class (and the cause of the lost partner).
//
// The world of every fixture: a PortalBox panel 400 x 200 u (its mid-quad in the plane y = 2048), and a wall
// 600 x 40 x 300 u whose near face stands 40 u behind the quad. Numbers are the fixture's own, chosen so each case sits
// well away from its thresholds (stated per case), never measured from a real order.

public static class SelfTest
{
    static readonly ModKey[] Mods = { ModKey.FromNameAndExtension("LbBase.esm"), ModKey.FromNameAndExtension("LbOcc.esp"),
                                      ModKey.FromNameAndExtension("LbCity.esp") };
    const string PanelPlugin = "LbOcc.esp";
    static FormKey K(int plugin, uint id) => new(Mods[plugin], id);

    static readonly FormKey Ws = K(0, 0x3C), Cell = K(0, 0xD74);
    // bases
    static readonly FormKey Wall = K(0, 0x100), Wall2 = K(0, 0x101), Long = K(0, 0x102), Cube = K(0, 0x103), Small = K(0, 0x104),
                            WallA = K(0, 0x105), WallB = K(0, 0x106), Big = K(0, 0x107), Mark = K(0, 0x108), MarkWall = K(0, 0x109),
                            NoGeom = K(0, 0x10A), Plane = K(0, 0x17), Trig = K(0, 0x10B);
    // references
    static readonly FormKey P = K(1, 0x800), W1 = K(0, 0x1000), W1b = K(0, 0x1001), Nb = K(0, 0x1002), M = K(0, 0x1100),
                            NewRef = K(2, 0x900), Missing = K(2, 0xDEAD);

    static readonly Vector3 PanelPos = new(2048, 2048, 100);
    static readonly Vector3 WallPos = new(2048, 2108, 100);

    sealed class Fx
    {
        public readonly List<(int Plugin, PlacedRef R)> Recs = new();
        public readonly Dictionary<FormKey, (string Type, string Edid, bool Marker, Vector3 Size, bool Model)> Bases = new();
        public readonly Dictionary<FormKey, (Vector3 Size, string Src)> CityGeomOverride = new();
        public float? LandBase, LandCity;

        public Fx()
        {
            Base(Wall, "Static", "FxWall", false, new(600, 40, 300));
            Base(Wall2, "Static", "FxWallNew", false, new(600, 40, 300));
            Base(Long, "Static", "FxLongWall", false, new(2000, 40, 300));
            Base(Cube, "Static", "FxCube", false, new(100, 100, 100));
            // A 100 x 10 x 100 plate: its near face 55 u behind the quad, so it backs only the samples within ~33 u of its
            // edge in the quad's plane — about a quarter of them (L1 used an 80 u cube, which backs half: too close to tauLost).
            Base(Small, "Static", "FxSmall", false, new(100, 10, 100));
            Base(WallA, "Static", "FxWallA", false, new(230, 40, 300));
            Base(WallB, "Static", "FxWallB", false, new(270, 40, 300));
            Base(Big, "Static", "FxBigRock", false, new(1000, 1000, 1000));
            Base(Mark, "Static", "FxEnableMarker", true, Vector3.Zero);
            Base(MarkWall, "Static", "FxMarkerWall", true, new(600, 40, 300));
            Base(NoGeom, "Static", "FxNoGeometry", false, Vector3.Zero);
            Base(Plane, "Static", "FxPlaneMarker", true, Vector3.Zero);
            // A trigger-style activator: no model, but bounds (the provider would hand back a box if asked).
            Base(Trig, "Activator", "FxSetStageTrigPlayerOnly", false, new(300, 300, 300), model: false);
        }

        void Base(FormKey k, string type, string edid, bool marker, Vector3 size, bool model = true) => Bases[k] = (type, edid, marker, size, model);

        public Fx Place(int plugin, FormKey key, FormKey baseKey, Vector3 pos, Vector3 rotRad = default, bool disabled = false,
                        bool deleted = false, FormKey? parent = null, bool opposite = false, PlacedPrimitiveInfo? prim = null)
        {
            Recs.Add((plugin, new PlacedRef(key, "PlacedObject", null, new Vec3(pos.X, pos.Y, pos.Z), new Vec3(rotRad.X, rotRad.Y, rotRad.Z),
                                            1f, baseKey, false, disabled, deleted, parent, opposite, Primitive: prim)));
            return this;
        }

        public Fx PortalPanel(int plugin = 1, bool disabled = false, Vector3? pos = null)
            => Place(plugin, P, Plane, pos ?? PanelPos, disabled: disabled, prim: new PlacedPrimitiveInfo("PortalBox", new Vec3(400, 16, 200)));

        public Fx BoxPanel(Vector3 pos, Vector3 size)
            => Place(1, P, Plane, pos, prim: new PlacedPrimitiveInfo("Box", new Vec3(size.X, size.Y, size.Z)));

        public Fx StandardWall(FormKey? baseKey = null, FormKey? parent = null, bool opposite = false, bool disabled = false)
            => Place(0, W1, baseKey ?? Wall, WallPos, disabled: disabled, parent: parent, opposite: opposite);

        static ArmGeometry? Box(Vector3 size, string src)
        {
            if (size == Vector3.Zero) return null;
            var h = size / 2;
            var soup = ContactIndex.BoxSoup(new Aabb(new Vec3(-h.X, -h.Y, -h.Z), new Vec3(h.X, h.Y, h.Z), false));
            return new ArmGeometry(soup, soup, 'm', src);
        }

        LeftBehindArm Arm(string name, int lastPlugin)
        {
            var arm = new LeftBehindArm(name);
            for (int p = 0; p <= lastPlugin; p++) arm.Plugins.Add(Mods[p].FileName);
            arm.WorldspaceName[Ws] = "FixtureWorld";
            foreach (var (k, b) in Bases)
            {
                arm.Index.BaseInfo[k] = (b.Type, b.Edid);
                if (b.Marker) arm.Index.BaseIsMarker.Add(k);
                if (b.Model) arm.Index.BaseMesh[k] = $"fixture/{b.Edid}.nif";
            }
            foreach (var (plugin, r) in Recs.Where(x => x.Plugin <= lastPlugin)) arm.Index.Record(plugin, Cell, Ws, r);
            bool city = lastPlugin == 2;
            arm.Geometry = k =>
            {
                if (city && CityGeomOverride.TryGetValue(k, out var o)) return Box(o.Size, o.Src);
                return Bases.TryGetValue(k, out var b) ? Box(b.Size, "vanilla:" + b.Edid) : null;
            };
            var land = city ? (LandCity ?? LandBase) : LandBase;
            if (land is { } h)
            {
                var grid = new float[33, 33];
                for (int x = 0; x < 33; x++) for (int y = 0; y < 33; y++) grid[x, y] = h;
                arm.Land[(Ws, 0, 0)] = grid;
            }
            return arm;
        }

        public (LeftBehindArm B, LeftBehindArm C) Build() => (Arm("base", 1), Arm("city", 2));
    }

    static int _pass, _fail;
    static readonly List<string> _failures = new();

    /// <summary>The BACKING-GAINED class, by its contract spelling.</summary>
    const string BackingGained = "BACKING-GAINED";

    /// <summary>Does the verdict credit the gained backing to <paramref name="refKey"/> (a FormKey, or LAND), changed in
    /// the way <paramref name="how"/> names? Read by property name so the case needs no field the rule lacks.</summary>
    static (bool Ok, string Why) Gained(PanelVerdict v, string refKey, string how)
    {
        if (v.GetType().GetProperty("GainedBy")?.GetValue(v) is not System.Collections.IEnumerable list)
            return (false, "the verdict carries no gainedBy");
        var seen = new List<string>();
        foreach (var g in list)
        {
            var t = g.GetType();
            string? r = t.GetProperty("Ref")?.GetValue(g) as string, h = t.GetProperty("How")?.GetValue(g) as string;
            var share = t.GetProperty("ShareOfGain")?.GetValue(g) as float?;
            seen.Add($"{r}:{h}:{share:0.00}");
            if (r == refKey && h == how && share > 0.5f) return (true, $"gainedBy {r} {h} share {share:0.00}");
        }
        return (false, $"gainedBy [{string.Join(", ", seen)}] lacks {refKey}:{how} with most of the gain");
    }

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"   PASS  {name,-62} {detail}"); }
        else { _fail++; _failures.Add(name); Console.WriteLine($"   FAIL  {name,-62} {detail}"); }
    }

    /// <summary>Classify the fixture's panel through the real path and check class and (optionally) the cause of the
    /// partner that was lost.</summary>
    static void Case(string name, Fx fx, string expectClass, string? expectCause = null, Func<PanelVerdict, (bool Ok, string Why)>? extra = null)
    {
        try
        {
            var (b, c) = fx.Build();
            var verdicts = LeftBehindRule.Run(b, c, PanelPlugin, Thresholds.Proposed);
            var v = verdicts.SingleOrDefault(x => x.Panel.Key == P);
            if (v is null) { Check(name, false, "the fixture's panel was not classified"); return; }
            bool ok = v.Class == expectClass;
            string detail = $"{v.Class} (expected {expectClass}); bB {v.BackingBase:0.00} bC {v.BackingCity:0.00} lost {v.LostShare:0.00}"
                          + (v.Reason is null ? "" : $"; {v.Reason}");
            if (ok && expectCause is not null)
            {
                bool causeOk = v.Partners.Any(p => p.Cause == expectCause && p.ShareOfBacking > 0);
                ok &= causeOk;
                detail += $"; causes [{string.Join(",", v.Partners.Select(p => $"{p.Ref}:{p.Cause}"))}] expected {expectCause}";
            }
            if (ok && extra is not null) { var (eo, why) = extra(v); ok &= eo; detail += "; " + why; }
            Check(name, ok, detail);
        }
        catch (Exception ex) { Check(name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }

    public static int Run()
    {
        Console.WriteLine("leftbehindprobe self-test: synthetic fixtures, no game data");
        var rotZ = (float)(0.5 * Math.PI / 180);

        // ---- shape: the samples lie on the panel as StationRule draws it ------------------------------------------
        try
        {
            var rot = new Vector3(0.3f, -0.2f, 1.1f);
            var portal = new Panel(P, Ws, "FixtureWorld", "PortalBox", PanelPos, rot, new Vector3(400, 16, 200));
            var s = LeftBehindRule.Samples(portal);
            var quad = StationRule.MidQuad(new PrimitiveGeometry("p", PrimitiveKind.PortalBox, portal.ExtentFull, portal.Position, rot, "w", "c"));
            var n = Vector3.Normalize(Vector3.Cross(quad[1] - quad[0], quad[3] - quad[0]));
            bool onPlane = s is { } ss && ss.Points.Length == 81 && ss.Points.All(p => MathF.Abs(Vector3.Dot(p - quad[0], n)) < 1e-2f);
            var centroid = s is { } s2 ? s2.Points.Aggregate(Vector3.Zero, (a, p) => a + p) / s2.Points.Length : Vector3.Zero;
            Check("SH1 PortalBox: 81 samples on StationRule.MidQuad's plane", onPlane && Vector3.Distance(centroid, PanelPos) < 1e-2f,
                  $"n={s?.Points.Length} centroid {centroid}");
            Check("SH2 PortalBox: one sample stands for 1/81 of the quad (u2)", s is { } s3 && MathF.Abs(s3.Weight - 400f * 200f / 81f) < 1e-2f && s3.Unit == "u2",
                  $"{s?.Weight} {s?.Unit}");
            var box = new Panel(P, Ws, "FixtureWorld", "Box", PanelPos, rot, new Vector3(200, 300, 400));
            var sb = LeftBehindRule.Samples(box);
            var corners = StationRule.Corners(new PrimitiveGeometry("b", PrimitiveKind.Box, box.ExtentFull, box.Position, rot, "w", "c"));
            var ax = corners[1] - corners[0]; var ay = corners[2] - corners[0]; var az = corners[4] - corners[0];
            bool inside = sb is { } bb && bb.Points.Length == 125 && bb.Points.All(p =>
            {
                var d = p - corners[0];
                float u = Vector3.Dot(d, ax) / ax.LengthSquared(), v = Vector3.Dot(d, ay) / ay.LengthSquared(), w = Vector3.Dot(d, az) / az.LengthSquared();
                return u > 0.05f && u < 0.95f && v > 0.05f && v < 0.95f && w > 0.05f && w < 0.95f;
            });
            Check("SH3 Box: 125 samples inside StationRule.Corners' box, cell-centred", inside && sb!.Value.Unit == "u3"
                  && MathF.Abs(sb.Value.Weight - 200f * 300f * 400f / 125f) < 1f, $"n={sb?.Points.Length} {sb?.Unit}");
            var plane = new Panel(P, Ws, "FixtureWorld", "Plane", PanelPos, rot, new Vector3(200, 0, 200));
            Check("SH4 an unmodelled primitive type has no samples", LeftBehindRule.Samples(plane) is null, "");
        }
        catch (Exception ex) { Check("SH shape cases", false, $"threw {ex.GetType().Name}: {ex.Message}"); }

        // ---- GATE-A LB1-LB7 ----------------------------------------------------------------------------------------
        Case("LB0 the standard wall, untouched: INTACT", new Fx().PortalPanel().StandardWall(), PanelClass.Intact, PartnerCause.Unchanged);
        Case("LB1 partner initially disabled, no parent", new Fx().PortalPanel().StandardWall().Place(2, W1, Wall, WallPos, disabled: true),
             PanelClass.LeftBehind, PartnerCause.Disabled);
        Case("LB2 partner moved 700 u along the panel normal", new Fx().PortalPanel().StandardWall().Place(2, W1, Wall, WallPos + new Vector3(0, 700, 0)),
             PanelClass.LeftBehind, PartnerCause.Moved,
             v => (v.Partners.Any(p => p.Cause == PartnerCause.Moved && MathF.Abs(p.MoveU - 700) < 0.5f), "moveU 700"));
        Case("LB3 partner nudged 4 u and turned 0.5 deg (must NOT flag)",
             new Fx().PortalPanel().StandardWall().Place(2, W1, Wall, WallPos + new Vector3(4, 0, 0), new Vector3(0, 0, rotZ)),
             PanelClass.Intact);
        Case("LB4 partner disabled, a new ref of another base at its pose",
             new Fx().PortalPanel().StandardWall().Place(2, W1, Wall, WallPos, disabled: true).Place(2, NewRef, Wall2, WallPos),
             PanelClass.ReBacked, null,
             v => (v.Partners.Any(p => p.ReplacedBy == NewRef.ToString()), $"replacedBy {NewRef}"));
        Case("LB5 a free-standing panel, nothing changed", new Fx().PortalPanel(), PanelClass.Unpaired);
        Case("LB6 the partner's neighbour disabled, the partner untouched",
             new Fx().PortalPanel().Place(0, W1, Long, new Vector3(2848, 2108, 100)).Place(0, Nb, Cube, new Vector3(2048, 2078, 100))
                     .Place(2, Nb, Cube, new Vector3(2048, 2078, 100), disabled: true),
             PanelClass.Intact);
        Case("LB7 the terrain under a terrain-backed panel lowered 300 u",
             new Fx { LandBase = 250f, LandCity = -50f }.PortalPanel(), PanelClass.LeftBehind, PartnerCause.Terrain,
             v => (v.Terrain == "compared", $"terrain '{v.Terrain}'"));

        // ---- review F1: the load-time enable chain -----------------------------------------------------------------
        Case("LB8 partner untouched, its enable parent disabled in the city arm",
             new Fx().PortalPanel().Place(0, M, Mark, new Vector3(2048, 3000, 0)).StandardWall(parent: M)
                     .Place(2, M, Mark, new Vector3(2048, 3000, 0), disabled: true),
             PanelClass.LeftBehind, PartnerCause.ParentDisabled);
        Case("LB9 opposite-state child of a disabled parent, own flag set: present",
             new Fx().PortalPanel().Place(0, M, Mark, new Vector3(2048, 3000, 0), disabled: true).StandardWall(parent: M, opposite: true, disabled: true),
             PanelClass.Intact, PartnerCause.Unchanged);
        Case("LB9b the same child after the city arm enables the parent",
             new Fx().PortalPanel().Place(0, M, Mark, new Vector3(2048, 3000, 0), disabled: true).StandardWall(parent: M, opposite: true, disabled: true)
                     .Place(2, M, Mark, new Vector3(2048, 3000, 0)),
             PanelClass.LeftBehind, PartnerCause.ParentDisabled);
        Case("LB10 partner's chain unresolvable in the city arm (parent never placed)",
             new Fx().PortalPanel().StandardWall().Place(2, W1, Wall, WallPos, parent: Missing),
             PanelClass.CannotAssess, null,
             v => (v.Reason is not null && v.Reason.Contains("unresolved", StringComparison.OrdinalIgnoreCase) && v.UnknownBackers == 1,
                   $"reason names the unresolved chain; unknownBackers {v.UnknownBackers}"));

        // ---- the other causes and classes ----------------------------------------------------------------------------
        Case("LB11 partner deleted", new Fx().PortalPanel().StandardWall().Place(2, W1, Wall, WallPos, deleted: true),
             PanelClass.LeftBehind, PartnerCause.Deleted);
        Case("LB12 partner base-swapped to a small object", new Fx().PortalPanel().StandardWall().Place(2, W1, Small, WallPos),
             PanelClass.LeftBehind, PartnerCause.BaseSwapped);
        Case("LB13 one of two walls disabled: a third of the backing lost",
             new Fx().PortalPanel().Place(0, W1, WallA, new Vector3(1913, 2108, 100)).Place(0, W1b, WallB, new Vector3(2163, 2108, 100))
                     .Place(2, W1, WallA, new Vector3(1913, 2108, 100), disabled: true),
             PanelClass.Partial, PartnerCause.Disabled);
        Case("LB14 a Box inside a closed solid (backed by inside, not distance), solid disabled",
             new Fx().BoxPanel(new Vector3(2048, 2048, 500), new Vector3(200, 200, 200)).Place(0, W1, Big, new Vector3(2048, 2048, 500))
                     .Place(2, W1, Big, new Vector3(2048, 2048, 500), disabled: true),
             PanelClass.LeftBehind, PartnerCause.Disabled);
        Case("LB14b the same Box and solid, untouched", new Fx().BoxPanel(new Vector3(2048, 2048, 500), new Vector3(200, 200, 200))
                     .Place(0, W1, Big, new Vector3(2048, 2048, 500)),
             PanelClass.Intact, null,
             // every sample is inside, including the ones on the solid's face diagonals (a crossing counted once)
             v => (v.BackingBase == 1f, $"bB {v.BackingBase:0.00} must be 1.00"));
        Case("LB15 the panel itself disabled by the city arm", new Fx().PortalPanel().StandardWall().PortalPanel(plugin: 2, disabled: true),
             PanelClass.CannotAssess);
        Case("LB16 a marker-flagged base never backs", new Fx().PortalPanel().StandardWall(baseKey: MarkWall), PanelClass.Unpaired);
        Case("LB17 a changed reference near the panel has no readable geometry",
             new Fx().PortalPanel().StandardWall().Place(0, Nb, NoGeom, new Vector3(2048, 2090, 100))
                     .Place(2, Nb, NoGeom, new Vector3(2048, 2790, 100)),
             PanelClass.CannotAssess);
        Case("LB19 a changed model-less trigger near the panel neither blocks nor backs",
             new Fx().PortalPanel().StandardWall().Place(0, Nb, Trig, new Vector3(2048, 2090, 100))
                     .Place(2, Nb, Trig, new Vector3(2048, 2790, 100)),
             PanelClass.Intact);
        Case("LB19b a model-less base with bounds never backs", new Fx().PortalPanel().Place(0, Nb, Trig, WallPos), PanelClass.Unpaired);
        var replaced = new Fx().PortalPanel().StandardWall();
        replaced.CityGeomOverride[Wall] = (new Vector3(40, 40, 40), "city:FxWall");
        Case("LB18 the partner's mesh replaced by the city arm's asset view", replaced, PanelClass.LeftBehind, PartnerCause.MeshReplaced);

        // ---- BACKING-GAINED: a panel with nothing behind it in the base arm that the city arm builds up to ------------
        // The class is asserted by its contract spelling and the credit read by name through reflection, so these cases
        // compile against a rule that has neither yet and fail there; the same text then checks the rule that has both.
        Case("BG1 open ground in the base arm, the city adds a wall at the panel", new Fx().PortalPanel().Place(2, NewRef, Wall2, WallPos),
             BackingGained, null, v => Gained(v, NewRef.ToString(), "new"));
        Case("BG2 a base wall 700 u away, moved by the city to the panel",
             new Fx().PortalPanel().Place(0, W1, Wall, WallPos + new Vector3(0, 700, 0)).Place(2, W1, Wall, WallPos),
             BackingGained, null, v => Gained(v, W1.ToString(), "moved"));
        Case("BG3 a wall disabled through its parent in the base arm, the city enables the parent",
             new Fx().PortalPanel().Place(0, M, Mark, new Vector3(2048, 3000, 0), disabled: true).StandardWall(parent: M)
                     .Place(2, M, Mark, new Vector3(2048, 3000, 0)),
             BackingGained, null, v => Gained(v, W1.ToString(), "parent-enabled"));
        Case("BG3b the same wall, its chain unresolvable in the city arm (parent never placed)",
             new Fx().PortalPanel().Place(0, M, Mark, new Vector3(2048, 3000, 0), disabled: true).StandardWall(parent: M)
                     .Place(2, W1, Wall, WallPos, parent: Missing),
             PanelClass.CannotAssess, null,
             v => (v.Reason is not null && v.Reason.Contains("unresolved", StringComparison.OrdinalIgnoreCase), "reason names the unresolved chain"));
        Case("BG4 the terrain far below the panel in the base arm, raised to it by the city",
             new Fx { LandBase = -500f, LandCity = 90f }.PortalPanel(), BackingGained, null, v => Gained(v, "LAND", "terrain"));
        Case("BG5 the city adds backing below tauPair: still UNPAIRED", new Fx().PortalPanel().Place(2, NewRef, Small, WallPos),
             PanelClass.Unpaired, null, v => (v.BackingCity > 0f && v.BackingCity < Thresholds.Proposed.TauPair, $"bC {v.BackingCity:0.00} in (0, tauPair)"));

        Console.WriteLine();
        Console.WriteLine($"self-test: {_pass} passed, {_fail} failed{(_fail > 0 ? " — " + string.Join("; ", _failures) : "")}");
        return _fail;
    }
}
