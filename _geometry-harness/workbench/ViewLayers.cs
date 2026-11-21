using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Mutagen.Bethesda.Plugins;
using SkyRender;
using ViewProbe;

namespace Workbench;

/// <summary>A layer of the 3D view that the toolbar above the view's buttons switches.</summary>
internal enum Layer { Navmesh, Borders, Markers, Planes, Terrain, Water, Grass, Trees, Clutter, Statics, Marks }

/// <summary>The window's choice of layers: one on or off per layer, kept across cell switches (the choice is the
/// window's, not the scene's). What each scene can draw is <see cref="SceneLayers"/>.</summary>
internal static class ViewLayers
{
    internal sealed record Info(Layer Layer, string Name, string Label, string Tip, bool Default);

    /// <summary>Every layer, in the toolbar's order, with the name a script or a state tool uses.</summary>
    internal static readonly IReadOnlyList<Info> All = new Info[]
    {
        new(Layer.Navmesh, "navmesh", "Navmesh", "the navmesh, as a translucent sheet with its triangle edges; red where a finding names it", true),
        new(Layer.Borders, "borders", "Cell borders", "the exterior cell grid: a gold curtain around this scene's cell (with its white dashed outline), pale blue around the cells loaded beside it", true),
        new(Layer.Markers, "markers", "Markers", "objects the game does not draw: editor markers as violet stems with an arrow the way they face, and objects with no model, such as most lights and sound markers, as yellow diamonds", false),
        new(Layer.Planes, "planes", "Occlusion", "occlusion planes: grey vanilla, gold inherited, magenta added, teal moved", false),
        new(Layer.Terrain, "terrain", "Terrain", "the landscape", true),
        new(Layer.Water, "water", "Water", "the water surfaces", true),
        new(Layer.Grass, "grass", "Grass", "the grass the landscape grows", true),
        new(Layer.Trees, "trees", "Trees", "trees and plants (tree and flora objects)", true),
        new(Layer.Clutter, "clutter", "Clutter", "items, containers, furniture and light fittings", true),
        new(Layer.Statics, "statics", "Statics", "buildings, rocks, doors and every other drawn object that is not a tree, a plant or clutter", true),
        new(Layer.Marks, "marks", "Marks", ConflictMarks.Legend + "; and the Results tab's boxes", Environment.GetEnvironmentVariable("WB_MARKS") != "off"),
    };

    static readonly Dictionary<Layer, bool> On = All.ToDictionary(i => i.Layer, i => i.Default);

    internal static Info Of(Layer l) => All.First(i => i.Layer == l);
    internal static bool IsOn(Layer l) => On[l];
    internal static void Choose(Layer l, bool on) { On[l] = on; if (l == Layer.Navmesh) WantsNavmesh = on; }

    /// <summary>The navmesh was switched on by hand: later cells whose load order leaves it out are asked for with it.</summary>
    internal static bool WantsNavmesh { get; set; }

    /// <summary>The navmesh mode a cell request carries for viewprobe: "filled" when the navmesh was asked for and the
    /// load order's own scenes leave it out; null keeps the run's.</summary>
    internal static string? NavFor(LoadOrderSpec order) => WantsNavmesh && !order.Navmesh ? NavAskedMode : null;
    /// <summary>The mode a cell asked for with its navmesh shows it in: the sheet and the wire.</summary>
    internal const string NavAskedMode = "both";

    /// <summary>Back to the defaults (a new window; the tests).</summary>
    internal static void Reset() { foreach (var i in All) On[i.Layer] = i.Default; WantsNavmesh = false; _pose = null; }

    /// <summary>A layer by the name a script or a state tool uses (any case, or its label), or the sentence refusing it.</summary>
    internal static (Layer? Layer, string? Why) Parse(string? name)
    {
        var n = (name ?? "").Trim();
        var hit = All.FirstOrDefault(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Label, n, StringComparison.OrdinalIgnoreCase));
        return hit is not null ? (hit.Layer, null) : (null, $"'{n}' is not a layer of the view; use one of {string.Join(", ", All.Select(i => i.Name))}.");
    }

    /// <summary>The script's <c>layer NAME on|off</c> step, or the sentence refusing it.</summary>
    internal static ((Layer Layer, bool On)? Step, string? Why) ParseStep(IReadOnlyList<string> a)
    {
        if (a.Count != 3) return (null, $"the layer step is 'layer NAME on|off', with NAME one of {string.Join(", ", All.Select(i => i.Name))}.");
        var (l, why) = Parse(a[1]);
        if (l is null) return (null, why);
        return OnOff(a[2]) is { } on ? ((l.Value, on), null) : (null, $"'{a[2]}' is not on or off.");
    }

    /// <summary>"on" or "off" as a script or a state tool says it; null for anything else.</summary>
    internal static bool? OnOff(string? word) => (word ?? "").Trim().ToLowerInvariant() switch { "on" or "true" or "show" => true, "off" or "false" or "hide" => false, _ => null };

    static readonly HashSet<string> TreeTypes = new(StringComparer.Ordinal) { "Tree", "Flora" };
    static readonly HashSet<string> ClutterTypes = new(StringComparer.Ordinal)
    {
        "MiscItem", "Weapon", "Armor", "Book", "Ingestible", "Ingredient", "Ammunition", "Key", "SoulGem", "Scroll", "Light", "Furniture", "Container",
    };

    /// <summary>The layer a drawn object belongs to, by its base's record type; an unknown type counts as a static.</summary>
    internal static Layer CategoryOf(string? baseType) =>
        baseType is null ? Layer.Statics : TreeTypes.Contains(baseType) ? Layer.Trees : ClutterTypes.Contains(baseType) ? Layer.Clutter : Layer.Statics;

    // the camera to keep when a cell is opened again for its navmesh
    static (string Cell, Vector3 Eye, float Yaw, float Pitch)? _pose;
    internal static void KeepPose(string cell, Vector3 eye, float yaw, float pitch) => _pose = (cell, eye, yaw, pitch);
    internal static (Vector3 Eye, float Yaw, float Pitch)? TakePose(string? cell)
    {
        var p = _pose; _pose = null;
        return p is { } q && string.Equals(q.Cell, cell, StringComparison.OrdinalIgnoreCase) ? (q.Eye, q.Yaw, q.Pitch) : null;
    }
}

/// <summary>What a scene was built with, read once when it arrives: the facts the layers' availability follows.</summary>
internal sealed record LayerFacts(
    bool Interior,
    bool NavBuilt,
    bool NavLeftOut,
    bool CanAskAgain,
    int Planes,
    int LandCells,
    int Undrawn,
    IReadOnlyDictionary<string, List<GlMesh>> Meshes,
    IReadOnlyList<RefOcclusion> Drawn,
    IReadOnlyDictionary<FormKey, string>? BaseTypes);

/// <summary>The switches a scene's layers act through: each the owner's own state, set, never a second copy of it.</summary>
internal sealed record LayerSwitches(
    Action<bool> Navmesh,
    Action<bool> Planes,
    Action<bool> Borders,
    Action<bool> Markers,
    Action<bool> Marks,
    Action<IEnumerable<GlMesh>, bool> Hide);

/// <summary>One scene's layers: which of them it can draw (and why not), and the switch each one sets.</summary>
internal sealed class SceneLayers
{
    readonly Dictionary<Layer, string?> _why = new();
    readonly Dictionary<Layer, Action<bool>> _switch = new();
    readonly Dictionary<Layer, int> _count = new();

    /// <summary>The navmesh is not in this scene, and the cell can be asked for again with it.</summary>
    internal bool NavLoadable { get; private set; }

    internal string? Why(Layer l) => _why.TryGetValue(l, out var w) ? w : "this layer is not in this scene.";
    internal bool Has(Layer l) => _why.TryGetValue(l, out var w) && w is null;
    internal bool Shown(Layer l) => Has(l) && ViewLayers.IsOn(l);
    internal int Count(Layer l) => _count.GetValueOrDefault(l);

    void Offer(Layer l, Action<bool> sw, int count = 0) { _why[l] = null; _switch[l] = sw; _count[l] = count; }
    void Lack(Layer l, string why) => _why[l] = why;

    /// <summary>The scene's layers from what it was built with.</summary>
    internal static SceneLayers From(LayerFacts f, LayerSwitches sw)
    {
        var s = new SceneLayers();
        if (f.NavBuilt) s.Offer(Layer.Navmesh, sw.Navmesh);
        else if (f.NavLeftOut && f.CanAskAgain)
        {
            s.Lack(Layer.Navmesh, "this cell was opened without its navmesh; press to open it again with it, which takes about as long as opening the cell.");
            s.NavLoadable = true;
        }
        else if (f.NavLeftOut) s.Lack(Layer.Navmesh, "this scene was built without its navmesh, and it was not opened from a tab that can ask for it again.");
        else s.Lack(Layer.Navmesh, "no navmesh is loaded in this cell or the cells around it.");

        if (f.Interior) s.Lack(Layer.Borders, "an interior has no cell grid, so there are no cell borders to draw.");
        else if (f.LandCells == 0) s.Lack(Layer.Borders, "no land is loaded around this cell, so the cell borders have no ground to stand on.");
        else s.Offer(Layer.Borders, sw.Borders, f.LandCells);

        if (f.Undrawn > 0) s.Offer(Layer.Markers, sw.Markers, f.Undrawn);
        else s.Lack(Layer.Markers, "the scene met no editor marker and no object without a model.");

        if (f.Planes > 0) s.Offer(Layer.Planes, sw.Planes, f.Planes);
        else s.Lack(Layer.Planes, "no occlusion planes stand in this cell or the cells around it.");

        void Meshes(Layer l, string key, string none)
        {
            if (f.Meshes.TryGetValue(key, out var m) && m.Count > 0) s.Offer(l, on => sw.Hide(m, !on), m.Count);
            else s.Lack(l, none);
        }
        Meshes(Layer.Terrain, "terrain", f.Interior ? "an interior has no landscape." : "no landscape is drawn in this scene.");
        Meshes(Layer.Water, "water", "no water surface is drawn in this scene.");
        Meshes(Layer.Grass, "grass", f.Interior ? "an interior grows no grass." : "no grass is drawn in this scene.");

        var byLayer = f.Drawn.GroupBy(r => ViewLayers.CategoryOf(f.BaseTypes is { } bt && bt.TryGetValue(r.Ref.BaseKey, out var t) ? t : null))
                             .ToDictionary(g => g.Key, g => g.ToList());
        void Objects(Layer l, string none)
        {
            if (byLayer.TryGetValue(l, out var refs) && refs.Count > 0)
            {
                var meshes = refs.SelectMany(r => r.Meshes).ToList();
                s.Offer(l, on => sw.Hide(meshes, !on), refs.Count);
            }
            else s.Lack(l, none);
        }
        Objects(Layer.Trees, "no tree or plant is drawn in this scene.");
        Objects(Layer.Clutter, "no item, container, furniture or light fitting is drawn in this scene.");
        Objects(Layer.Statics, "no building, rock or other static is drawn in this scene.");

        s.Offer(Layer.Marks, sw.Marks);
        return s;
    }

    /// <summary>Set every layer this scene has to the window's choice (a scene arriving).</summary>
    internal void ApplyAll() { foreach (var (l, sw) in _switch) sw(ViewLayers.IsOn(l)); }

    /// <summary>Switch a layer as the window's choice and draw it so; one sentence says what changed. A layer this scene
    /// cannot draw is refused with the reason (the navmesh's reload is the caller's, see <see cref="NavLoadable"/>).</summary>
    internal string Set(Layer l, bool on)
    {
        var info = ViewLayers.Of(l);
        if (!Has(l)) throw new InvalidOperationException($"{info.Label} cannot be shown here: {Why(l)}");
        ViewLayers.Choose(l, on);
        _switch[l](on);
        return $"{info.Label} {(on ? "shown" : "hidden")}" + (Count(l) > 0 ? $" ({Count(l)} {Unit(l)})" : "");
    }

    static string Unit(Layer l) => l switch
    {
        Layer.Borders => "cell(s) of land",
        Layer.Markers => "marker(s) and object(s) without a model",
        Layer.Planes => "plane(s)",
        Layer.Terrain or Layer.Water or Layer.Grass => "mesh(es)",
        _ => "object(s)",
    };

    /// <summary>The state readout: each layer, whether this scene has it, whether the window has it on, and why not.</summary>
    internal object State() => ViewLayers.All.Select(i => new
    {
        layer = i.Name, label = i.Label, available = Has(i.Layer), on = ViewLayers.IsOn(i.Layer), shown = Shown(i.Layer),
        count = Count(i.Layer), why = Has(i.Layer) ? null : Why(i.Layer), canLoad = i.Layer == Layer.Navmesh && NavLoadable ? true : (bool?)null,
    }).ToList();
}

/// <summary>The toolbar: one compact row of toggle buttons, one per layer, grouped; each reflects the scene and the
/// window's choice, and a button for a layer this scene cannot draw is disabled with the reason as its tooltip.</summary>
internal sealed class LayerBar
{
    readonly Dictionary<Layer, ToggleButton> _buttons = new();
    readonly Func<SceneLayers> _scene;
    internal UIElement Element { get; }

    internal LayerBar(Func<SceneLayers> scene, Action<Layer, bool> press)
    {
        _scene = scene;
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 0) };
        row.Children.Add(new TextBlock { Text = "Show:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0), Foreground = Theme.Brush(Theme.Dim) });
        Layer[] gap = { Layer.Terrain, Layer.Marks };       // overlays | the scene's own geometry | marks
        foreach (var i in ViewLayers.All)
        {
            var b = new ToggleButton { Content = i.Label, Margin = new Thickness(gap.Contains(i.Layer) ? 12 : 2, 2, 2, 2), Padding = new Thickness(6, 1, 6, 1), Focusable = false };
            ToolTipService.SetShowOnDisabled(b, true);
            var layer = i.Layer;
            b.Click += (_, _) => press(layer, b.IsChecked == true);
            _buttons[layer] = b;
            row.Children.Add(b);
        }
        Element = row;
        Refresh();
    }

    /// <summary>Each button from the scene and the choice: checked while its layer is drawn.</summary>
    internal void Refresh(string? loading = null)
    {
        var s = _scene();
        foreach (var (l, b) in _buttons)
        {
            var info = ViewLayers.Of(l);
            bool has = s.Has(l), load = l == Layer.Navmesh && s.NavLoadable;
            b.IsEnabled = has || load;
            b.IsChecked = s.Shown(l);
            b.Content = l == Layer.Navmesh && loading is not null ? "Navmesh (loading)" : info.Label;
            b.ToolTip = has ? $"{Capital(info.Tip)} ({(ViewLayers.IsOn(l) ? "shown; press to hide" : "hidden; press to show")})."
                      : load ? Capital(s.Why(l)!) : $"{info.Label}: {s.Why(l)}" + (ViewLayers.IsOn(l) && l != Layer.Navmesh ? " It is on, and shows in a scene that has it." : "");
        }
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
