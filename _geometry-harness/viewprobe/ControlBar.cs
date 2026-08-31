using OpenTK.Graphics.OpenGL4;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Globalization;
using Color = SixLabors.ImageSharp.Color;
using PointF = SixLabors.ImageSharp.PointF;
using RectangleF = SixLabors.ImageSharp.RectangleF;

namespace ViewProbe;

// THE CONTROL BAR — the fly-through's keyboard surface, made visible and clickable.
//
// WHY IT EXISTS. Every live control in InteractiveViewer was a key with no on-screen presence.
// The HUD reported the RESULT of pressing one ("nav both", "clip z 128..2048") but never that the
// control existed or which key reached it, and the help that would have said so went to the
// CONSOLE — a window the person flying is not looking at. "H for help does nothing" was the exact
// report, and it was accurate: the key worked, the answer just arrived somewhere else. So the
// feature set was discoverable only by reading the source.
//
// ONE PIECE OF STATE, TWO HANDLES. Every widget reads and writes the loop's own variables through
// the get/set delegates it was built with — it does not keep a copy. So pressing the key moves
// the widget, dragging the widget is what the key would have done, and the two can never drift
// apart. That is the whole reason the bindings are delegates rather than values.
//
// NUMBERS ARE TYPEABLE, NOT JUST DRAGGABLE. A slider alone cannot hit "clip ceiling 2048"; a box
// alone cannot sweep a ceiling down through a roof and watch it melt. Every numeric control here
// is BOTH — one value, a track for the sweep and a box for the exact figure, each showing what
// the other did.
//
// NO NEW UI DEPENDENCY. The bar is drawn the way the HUD caption already was: laid out and
// rasterised with ImageSharp into one RGBA image, uploaded as a texture, drawn as a screen-space
// quad. That also keeps the widget layer testable off a GL context — RenderImage() is pure
// ImageSharp and hands back the bitmap, which is how this layout is checked without Skyrim.

static class UiTheme
{
    public static readonly Color Panel     = Color.FromRgba(8, 10, 14, 234);
    public static readonly Color PanelEdge = Color.FromRgba(70, 78, 92, 255);
    public static readonly Color Item      = Color.FromRgba(34, 38, 47, 255);
    public static readonly Color ItemHot   = Color.FromRgba(56, 62, 75, 255);
    public static readonly Color ItemOn    = Color.FromRgba(36, 92, 148, 255);
    public static readonly Color ItemOnHot = Color.FromRgba(50, 116, 182, 255);
    public static readonly Color ItemDead  = Color.FromRgba(22, 24, 29, 255);
    public static readonly Color Border    = Color.FromRgba(96, 104, 120, 255);
    public static readonly Color BorderOff = Color.FromRgba(52, 56, 65, 255);
    public static readonly Color BorderHot = Color.FromRgba(126, 194, 255, 255);
    public static readonly Color Text      = Color.FromRgb(238, 241, 246);
    public static readonly Color TextDim   = Color.FromRgb(170, 178, 192);
    public static readonly Color TextOff   = Color.FromRgb(96, 102, 114);
    public static readonly Color TextBad   = Color.FromRgb(255, 150, 150);
    public static readonly Color Track     = Color.FromRgba(18, 21, 27, 255);
    public static readonly Color TrackFill = Color.FromRgba(58, 110, 168, 255);
    public static readonly Color Knob      = Color.FromRgb(226, 232, 242);
    public static readonly Color Caret     = Color.FromRgb(126, 194, 255);
}

/// <summary>One widget. Rect is filled by Layout in panel pixels; everything else is a live
/// binding to the loop's own state, so the key and the widget cannot disagree.</summary>
abstract class UiItem
{
    public RectangleF Rect;
    /// <summary>Start a new row before this item, whatever room is left on the current one.</summary>
    public bool Break;
    /// <summary>Stays on screen when the bar is collapsed.</summary>
    public bool Pinned;
    /// <summary>Off the bar entirely for now — not greyed, GONE, taking its space with it. The
    /// distinction matters: a control that exists but cannot act stays visible with its reason
    /// (DisabledWhy), while a readout with nothing to report should not hold a row open.</summary>
    public Func<bool>? Hidden;
    /// <summary>A non-empty return greys the widget out and is the sentence it prints when
    /// clicked. A run with no navmesh must still SHOW that the display control exists and say why
    /// it does nothing — hiding it would make an absent feature look like an absent scene.</summary>
    public Func<string>? DisabledWhy;
    public string Why => DisabledWhy?.Invoke() ?? "";
    public bool Live => Why.Length == 0;

    public abstract float Measure(ControlBar b);
    public abstract void Paint(ControlBar b, IImageProcessingContext ctx);
    /// <summary>LMB went down inside Rect. Return true to keep receiving OnDrag until release.</summary>
    public virtual bool OnPress(ControlBar b, float x, float y) => false;
    public virtual void OnDrag(ControlBar b, float x, float y) { }
    public virtual void OnRelease(ControlBar b) { }
    /// <summary>Everything that can change what this widget draws, in one string. The panel
    /// rasterises only when the concatenation over all visible items changes, so a 60 fps loop
    /// pays for text layout on the frames that actually differ.</summary>
    public abstract string StateKey(ControlBar b);
}

/// <summary>A clickable box. IsOn, when given, paints it as a lit toggle.</summary>
sealed class UiButton : UiItem
{
    public required Func<string> Text;
    public required Action OnClick;
    public Func<bool>? IsOn;

    public override float Measure(ControlBar b) => b.TextW(Text()) + ControlBar.BoxPadX * 2;

    public override void Paint(ControlBar b, IImageProcessingContext ctx)
    {
        bool hot = b.Hot(this);
        b.PaintBox(ctx, Rect, IsOn?.Invoke() ?? false, hot, Live);
        b.PaintText(ctx, Text(), Rect.X + ControlBar.BoxPadX, Rect.Y, Rect.Height,
                    Live ? UiTheme.Text : UiTheme.TextOff);
    }

    public override bool OnPress(ControlBar b, float x, float y)
    {
        if (Live) OnClick(); else b.Report(Why);
        return false;
    }

    public override string StateKey(ControlBar b)
        => "B" + Text() + (IsOn?.Invoke() == true ? "^" : "") + (b.Hot(this) ? "~" : "") + Why;
}

/// <summary>Static text between widgets — a group heading, or the worklist shot's own sentence.</summary>
sealed class UiLabel : UiItem
{
    public required Func<string> Text;
    public bool Bright;
    public override float Measure(ControlBar b) => b.TextW(Text()) + 6f;
    public override void Paint(ControlBar b, IImageProcessingContext ctx)
        => b.PaintText(ctx, Text(), Rect.X + 3f, Rect.Y, Rect.Height, Bright ? UiTheme.Text : UiTheme.TextDim);
    public override string StateKey(ControlBar b) => "L" + Text();
}

/// <summary>A labelled row of mutually exclusive choices — the navmesh display's four modes, all
/// four on screen at once. A cycle button would show one; the point of the bar is that the
/// alternatives are visible, not one keypress into a sequence you have to remember.</summary>
sealed class UiSegment : UiItem
{
    public required string Label;
    public required string[] Options;
    public required Func<string> Get;
    public required Action<string> Set;
    float[] _w = Array.Empty<float>();

    float FirstX(ControlBar b) => Rect.X + b.TextW(Label) + 6f;

    public override float Measure(ControlBar b)
    {
        _w = Options.Select(o => b.TextW(o) + ControlBar.BoxPadX * 2).ToArray();
        return b.TextW(Label) + 6f + _w.Sum() + (Options.Length - 1) * 2f;
    }

    /// <summary>Which option the cursor is over, or -1. Also feeds StateKey, so hovering one
    /// segment repaints the bar exactly once rather than on every mouse move across it.</summary>
    public int HotIndex(ControlBar b)
    {
        if (!b.Hot(this)) return -1;
        float x = FirstX(b);
        for (int i = 0; i < Options.Length && i < _w.Length; i++)
        {
            if (b.Mouse.X >= x && b.Mouse.X < x + _w[i]) return i;
            x += _w[i] + 2f;
        }
        return -1;
    }

    public override void Paint(ControlBar b, IImageProcessingContext ctx)
    {
        b.PaintText(ctx, Label, Rect.X, Rect.Y, Rect.Height, Live ? UiTheme.TextDim : UiTheme.TextOff);
        float x = FirstX(b);
        string cur = Get();
        int hotIdx = HotIndex(b);
        for (int i = 0; i < Options.Length; i++)
        {
            var r = new RectangleF(x, Rect.Y, _w[i], Rect.Height);
            b.PaintBox(ctx, r, Options[i] == cur, i == hotIdx, Live);
            b.PaintText(ctx, Options[i], r.X + ControlBar.BoxPadX, r.Y, r.Height,
                        !Live ? UiTheme.TextOff : Options[i] == cur ? UiTheme.Text : UiTheme.TextDim);
            x += _w[i] + 2f;
        }
    }

    public override bool OnPress(ControlBar b, float x, float y)
    {
        if (!Live) { b.Report(Why); return false; }
        int i = HotIndex(b);
        if (i >= 0) Set(Options[i]);
        return false;
    }

    public override string StateKey(ControlBar b) => "S" + Label + "=" + Get() + "~" + HotIndex(b) + Why;
}

/// <summary>Arrows around a readout — the worklist's shot and cell paging, which is a step, not a
/// value: there is no continuum between shot 2 and shot 3 to drag along.</summary>
sealed class UiStepper : UiItem
{
    public required Func<string> Text;
    public required Action<int> Step;
    const float ArrowW = 26f;

    public override float Measure(ControlBar b) => ArrowW * 2 + b.TextW(Text()) + 16f;

    int HotArrow(ControlBar b)
        => !b.Hot(this) ? 0 : b.Mouse.X < Rect.X + ArrowW ? -1 : b.Mouse.X >= Rect.Right - ArrowW ? 1 : 0;

    public override void Paint(ControlBar b, IImageProcessingContext ctx)
    {
        int ha = HotArrow(b);
        var left = new RectangleF(Rect.X, Rect.Y, ArrowW, Rect.Height);
        var right = new RectangleF(Rect.Right - ArrowW, Rect.Y, ArrowW, Rect.Height);
        b.PaintBox(ctx, left, false, ha < 0, Live);
        b.PaintBox(ctx, right, false, ha > 0, Live);
        var tc = Live ? UiTheme.Text : UiTheme.TextOff;
        // Drawn as triangles rather than typed as glyphs. The bold faces this reaches for do not
        // all carry U+25C0/U+25B6 — measured: they came out as tofu boxes on this machine — and an
        // arrow that renders as a missing-glyph square is worse than no arrow at all.
        b.PaintArrow(ctx, left, -1, tc);
        b.PaintArrow(ctx, right, +1, tc);
        b.PaintText(ctx, Text(), Rect.X + ArrowW + 8f, Rect.Y, Rect.Height,
                    Live ? UiTheme.TextDim : UiTheme.TextOff);
    }

    public override bool OnPress(ControlBar b, float x, float y)
    {
        if (!Live) { b.Report(Why); return false; }
        int a = HotArrow(b);
        if (a != 0) Step(a);
        return false;
    }

    public override string StateKey(ControlBar b) => "P" + Text() + "~" + HotArrow(b) + Why;
}

/// <summary>ONE value with two handles on it: a track to sweep and a box to type. Not two
/// controls that happen to agree — the track writes the same setter the box commits to, and both
/// read the same getter, so whichever key also writes it moves them both.
///
/// Nullable because two of these have a genuine OFF state that is not a number: a clip ceiling
/// parked at the top of the scene still hides whatever pokes through it, while "no ceiling" hides
/// nothing. The box then shows "off" and the track drops its knob.</summary>
sealed class UiNumber : UiItem
{
    public required string Label;
    public string Unit = "";
    public required Func<float?> Get;
    public required Action<float?> Set;
    public required float Min, Max;
    /// <summary>Map the track logarithmically. Fly speed spans 20..20000 u/s: on a linear track
    /// the entire usable indoor range would live in the first two pixels.</summary>
    public bool Log;
    /// <summary>Committing an empty box clears the value instead of reverting it.</summary>
    public bool Nullable;
    public string Format = "0.###";
    /// <summary>Printed on commit — the paste-ready flag, so a number found by dragging carries
    /// to a static render exactly the way the keys' own echoes already do.</summary>
    public Func<float?, string>? Echo;
    /// <summary>Wheel step. 0 uses the same 1.25x ladder the scroll and the , . keys already use
    /// for speed and sensitivity; a positive value steps linearly by that much, which is what a
    /// clip height in game units wants.</summary>
    public float ScrollStep;
    /// <summary>What an OFF value becomes when the wheel first touches it. The clip keys already
    /// work this way — "each starts at the camera's z on first touch" — and the wheel would be
    /// the one route into the control that did not.</summary>
    public Func<float>? Seed;

    public const float TrackW = 148f, BoxW = 76f;

    float LabelW(ControlBar b) => b.TextW(Label) + 6f;
    float TrackX(ControlBar b) => Rect.X + LabelW(b);
    float BoxX(ControlBar b) => TrackX(b) + TrackW + 6f;

    public override float Measure(ControlBar b)
        => LabelW(b) + TrackW + 6f + BoxW + (Unit.Length > 0 ? b.TextW(Unit) + 5f : 0f);

    public float ToNorm(float v)
    {
        v = Math.Clamp(v, Min, Max);
        return Log ? (MathF.Log(v) - MathF.Log(Min)) / (MathF.Log(Max) - MathF.Log(Min))
                   : (v - Min) / (Max - Min);
    }
    public float FromNorm(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Log ? MathF.Exp(MathF.Log(Min) + t * (MathF.Log(Max) - MathF.Log(Min)))
                   : Min + t * (Max - Min);
    }

    public string Display(ControlBar b)
        => b.Editing == this ? b.EditText
         : Get() is { } v ? v.ToString(Format, CultureInfo.InvariantCulture)
         : "off";

    public override void Paint(ControlBar b, IImageProcessingContext ctx)
    {
        b.PaintText(ctx, Label, Rect.X, Rect.Y, Rect.Height, Live ? UiTheme.TextDim : UiTheme.TextOff);

        // Track: a 6 px rail with the part left of the knob filled, so the value reads at a
        // glance from the fill length even when the knob is against an edge.
        float tx = TrackX(b), cy = Rect.Y + Rect.Height / 2f;
        var rail = new RectangleF(tx, cy - 3f, TrackW, 6f);
        b.PaintRect(ctx, rail, UiTheme.Track);
        b.PaintOutline(ctx, rail, Live ? UiTheme.Border : UiTheme.BorderOff);
        if (Live && Get() is { } val)
        {
            float t = ToNorm(val);
            if (t > 0f) b.PaintRect(ctx, new RectangleF(tx, cy - 3f, TrackW * t, 6f), UiTheme.TrackFill);
            b.PaintRect(ctx, new RectangleF(tx + TrackW * t - 4f, Rect.Y + 1f, 8f, Rect.Height - 2f), UiTheme.Knob);
        }

        // Box: focus is a border colour plus a caret, never a fill change — the value under the
        // caret has to stay as legible while typing as it was before.
        var box = new RectangleF(BoxX(b), Rect.Y, BoxW, Rect.Height);
        bool focus = b.Editing == this;
        b.PaintRect(ctx, box, Live ? UiTheme.ItemDead : UiTheme.Item);
        b.PaintOutline(ctx, box, !Live ? UiTheme.BorderOff : focus ? UiTheme.BorderHot : UiTheme.Border);
        string shown = Display(b);
        b.PaintText(ctx, shown, box.X + 6f, box.Y, box.Height,
                    !Live ? UiTheme.TextOff : focus && b.EditBad ? UiTheme.TextBad : UiTheme.Text);
        if (focus)
            b.PaintRect(ctx, new RectangleF(MathF.Min(box.X + 7f + b.TextW(shown), box.Right - 4f),
                                            box.Y + 3f, 2f, box.Height - 6f), UiTheme.Caret);
        if (Unit.Length > 0)
            b.PaintText(ctx, Unit, box.Right + 5f, Rect.Y, Rect.Height, Live ? UiTheme.TextDim : UiTheme.TextOff);
    }

    public override bool OnPress(ControlBar b, float x, float y)
    {
        if (!Live) { b.Report(Why); return false; }
        if (x >= BoxX(b) && x < BoxX(b) + BoxW) { b.BeginEdit(this); return false; }
        if (x >= TrackX(b) - 5f && x <= TrackX(b) + TrackW + 5f)
        {
            OnDrag(b, x, y);
            return true;                        // grab, so the sweep survives leaving the track
        }
        return false;
    }

    public override void OnDrag(ControlBar b, float x, float y) => Set(FromNorm((x - TrackX(b)) / TrackW));
    public override void OnRelease(ControlBar b) => b.EchoValue(this);

    public override string StateKey(ControlBar b)
        => "N" + Label + "=" + Display(b) + (b.Editing == this ? b.EditBad ? "|!" : "|_" : "") + Why;
}

/// <summary>A labelled value that copies itself when clicked. Reading a FormID off a caption and
/// retyping it into a query is the single most common thing anyone does with a pick, and retyping
/// is where the digit gets dropped.
///
/// It ELIDES from the front and copies WHOLE: a mesh path is identified by its tail, so
/// `...\Whiterun\WRHouse01.nif` still reads, while what reaches the clipboard is always the full
/// value the shortening was hiding.</summary>
sealed class UiCopyField : UiItem
{
    public required string Label;
    public required Func<string> Value;
    public required Action<string> Copy;
    /// <summary>Widest the value box may get before it starts eliding.</summary>
    public float MaxW = 460f;
    /// <summary>Elide from the END, keeping the head. A FormKey is identified by the hex id it
    /// STARTS with and padded by a plugin name that can run to forty characters, so shortening it
    /// from the front — right for a mesh path — throws away the only part anyone reads.</summary>
    public bool KeepHead;

    float LabelW(ControlBar b) => Label.Length == 0 ? 0f : b.TextW(Label) + 6f;

    public string Shown(ControlBar b)
    {
        var v = Value();
        float full = b.TextW(v);
        if (full <= MaxW || v.Length < 4) return v;
        // Proportional first cut, then walk in — a few measurements rather than one per character.
        int keep = Math.Max(1, (int)(v.Length * MaxW / full) - 3);
        if (KeepHead)
        {
            v = v[..keep];
            while (v.Length > 1 && b.TextW(v + "...") > MaxW) v = v[..^1];
            return v + "...";
        }
        v = v[^keep..];
        while (v.Length > 1 && b.TextW("..." + v) > MaxW) v = v[1..];
        // Cut at a folder boundary when one is close, so a path breaks BETWEEN its parts rather
        // than through the middle of a word — a reader should not have to parse a fragment before
        // they can start.
        int sep = v.IndexOfAny(new[] { '\\', '/' });
        if (sep >= 0 && sep < 12) v = v[(sep + 1)..];
        return "..." + v;
    }

    public override float Measure(ControlBar b)
        => LabelW(b) + b.TextW(Shown(b)) + ControlBar.BoxPadX * 2;

    public override void Paint(ControlBar b, IImageProcessingContext ctx)
    {
        bool hot = b.Hot(this);
        if (Label.Length > 0)
            b.PaintText(ctx, Label, Rect.X, Rect.Y, Rect.Height, UiTheme.TextDim);
        var box = new RectangleF(Rect.X + LabelW(b), Rect.Y, Rect.Width - LabelW(b), Rect.Height);
        b.PaintBox(ctx, box, false, hot, true);
        b.PaintText(ctx, Shown(b), box.X + ControlBar.BoxPadX, box.Y, box.Height,
                    hot ? UiTheme.BorderHot : UiTheme.Text);
    }

    public override bool OnPress(ControlBar b, float x, float y)
    {
        Copy(Value());
        return false;
    }

    public override string StateKey(ControlBar b) => "C" + Label + "=" + Value() + (b.Hot(this) ? "~" : "");
}

/// <summary>The panel: layout, rasterisation, one GL texture, and the routing that decides
/// whether a click belongs to a widget or to the world behind it.</summary>
sealed class ControlBar : IDisposable
{
    public const float FontPx = 18f, RowH = 28f, RowGap = 5f, PadX = 10f, PadY = 8f, ItemGap = 8f, BoxPadX = 9f;

    readonly List<UiItem> _items = new();
    readonly Font? _font;
    readonly TextOptions? _to;
    int _tex;
    int _builtW, _builtH;
    string _builtKey = "";
    UiItem? _grab;

    public int Width { get; private set; } = 1;
    public int Height { get; private set; } = (int)(RowH + PadY * 2);
    public bool Collapsed;
    /// <summary>Cursor in panel space. Set every frame by the loop, whether or not it is inside.</summary>
    public (float X, float Y) Mouse = (-1e6f, -1e6f);
    public UiNumber? Editing { get; private set; }
    public string EditText { get; private set; } = "";
    public bool EditBad { get; private set; }
    public bool IsEditing => Editing is not null;
    /// <summary>Where a widget's console echo goes. The bar never touches Console itself: the
    /// loop owns that stream and indents its lines, and a widget printing directly would speak in
    /// a different voice from the key doing the same job.</summary>
    public Action<string> Report = _ => { };

    public ControlBar()
    {
        foreach (var fam in new[] { "Segoe UI", "Arial", "Verdana" })
            if (SystemFonts.TryGet(fam, out var f)) { _font = f.CreateFont(FontPx, FontStyle.Bold); break; }
        if (_font is null)
            foreach (var fam in SystemFonts.Families) { _font = fam.CreateFont(FontPx, FontStyle.Bold); break; }
        if (_font is not null) _to = new TextOptions(_font);
    }

    public ControlBar Add(UiItem it) { _items.Add(it); return this; }
    public ControlBar Row(UiItem it) { it.Break = true; _items.Add(it); return this; }

    IEnumerable<UiItem> Shown => (Collapsed ? _items.Where(i => i.Pinned) : _items)
                                 .Where(i => i.Hidden?.Invoke() != true);

    // Layout runs every UpdateFrame — a rect that does not exist yet cannot be clicked — and
    // shaping a string is the expensive part of it. Almost every string here repeats frame to
    // frame (labels never change; values change slowly), so memoising the advance takes the whole
    // layout from 0.65 ms to noise. Measured on this machine, 1440-wide bar, 18 widgets.
    readonly Dictionary<string, float> _textW = new();
    public float TextW(string s)
    {
        if (_to is null || s.Length == 0) return s.Length * FontPx * 0.55f;
        if (_textW.TryGetValue(s, out var w)) return w;
        if (_textW.Count > 4096) _textW.Clear();          // a value box can mint new strings forever
        return _textW[s] = TextMeasurer.MeasureAdvance(s, _to).Width;
    }

    public bool Hot(UiItem it) => Mouse.X >= it.Rect.X && Mouse.X < it.Rect.Right
                               && Mouse.Y >= it.Rect.Y && Mouse.Y < it.Rect.Bottom;
    public bool Contains(float x, float y) => x >= 0 && x < Width && y >= 0 && y < Height;

    // TEXT IS THE WHOLE COST OF A REBUILD, so each string is rasterised once and thereafter
    // blitted. Measured on this machine: 40 DrawText calls over a 1440x143 panel take 6.4 ms, and
    // everything else in a rebuild — the allocation, the background, sixty box fills, the pixel
    // copy for the upload — adds up to 0.03 ms. That 6.4 ms would land on every frame of a slider
    // drag or a held clip key, which is exactly when the picture needs to stay smooth.
    //
    // Keyed on the colour as well as the text: the same word is drawn white when a segment is
    // selected and dim when it is not, and those are different pixels.
    readonly Dictionary<(string Text, uint Argb), Image<Rgba32>> _rasters = new();

    Image<Rgba32>? TextRaster(string s, Color col)
    {
        var key = (s, col.ToPixel<Rgba32>().PackedValue);
        if (_rasters.TryGetValue(key, out var have)) return have;
        if (_font is null) return null;
        // A number box mints a new string on every keystroke and every drag frame, so the cache
        // is capped rather than left to grow for the life of the window.
        if (_rasters.Count > 512) { foreach (var im in _rasters.Values) im.Dispose(); _rasters.Clear(); }
        var raster = new Image<Rgba32>(Math.Max((int)MathF.Ceiling(TextW(s)) + 2, 1),
                                       Math.Max((int)MathF.Ceiling(FontPx * 1.7f), 1));
        raster.Mutate(c => c.DrawText(s, _font, col, new PointF(0, 0)));
        return _rasters[key] = raster;
    }

    /// <summary>Vertically centre one line in a box. Centring on the em box rather than on each
    /// string's own measured bounds is what keeps a row of boxes on one optical line whether or
    /// not their particular text happens to have descenders.</summary>
    public void PaintText(IImageProcessingContext ctx, string s, float x, float boxY, float boxH, Color col)
    {
        if (s.Length == 0 || TextRaster(s, col) is not { } raster) return;
        // DrawImage clips rather than throwing when the raster overhangs the panel (verified
        // against this ImageSharp build, including a wholly off-panel location).
        ctx.DrawImage(raster, new Point((int)MathF.Round(x),
                                        (int)MathF.Round(boxY + (boxH - FontPx * 1.32f) / 2f)), 1f);
    }

    /// <summary>A solid triangle centred in a box, pointing left (-1) or right (+1).</summary>
    public void PaintArrow(IImageProcessingContext ctx, RectangleF box, int dir, Color col)
    {
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f, w = 4.5f, h = 6f;
        ctx.Fill(col, new Polygon(new[]
        {
            new PointF(cx - dir * w, cy - h),          // base, on the side the arrow comes FROM
            new PointF(cx - dir * w, cy + h),
            new PointF(cx + dir * w, cy),               // apex, pointing the way it steps
        }));
    }

    // Axis-aligned rectangles want neither antialiasing nor a stroked path: the edges land on
    // pixel boundaries, so smoothing them only blurs them, and stroking a rect outline costs
    // several times what four 1 px fills do. Measured: it is most of the rasterise time.
    static readonly DrawingOptions Crisp = new() { GraphicsOptions = new GraphicsOptions { Antialias = false } };

    public void PaintRect(IImageProcessingContext ctx, RectangleF r, Color col)
        => ctx.Fill(Crisp, col, new RectangularPolygon(r));

    public void PaintOutline(IImageProcessingContext ctx, RectangleF r, Color col)
    {
        PaintRect(ctx, new RectangleF(r.X, r.Y, r.Width, 1f), col);
        PaintRect(ctx, new RectangleF(r.X, r.Bottom - 1f, r.Width, 1f), col);
        PaintRect(ctx, new RectangleF(r.X, r.Y, 1f, r.Height), col);
        PaintRect(ctx, new RectangleF(r.Right - 1f, r.Y, 1f, r.Height), col);
    }

    public void PaintBox(IImageProcessingContext ctx, RectangleF r, bool on, bool hot, bool live)
    {
        PaintRect(ctx, r, !live ? UiTheme.ItemDead
                        : on ? (hot ? UiTheme.ItemOnHot : UiTheme.ItemOn)
                        : (hot ? UiTheme.ItemHot : UiTheme.Item));
        PaintOutline(ctx, r, live ? (hot ? UiTheme.BorderHot : UiTheme.Border) : UiTheme.BorderOff);
    }

    public void EchoValue(UiNumber n)
    {
        if (n.Echo is { } e && e(n.Get()) is { Length: > 0 } line) Report(line);
    }

    // ---- layout -------------------------------------------------------------------------
    // Flow, with wrap. Items are placed left to right and drop to the next row when the one they
    // are on runs out — so the bar is correct at any window width instead of only at 1440, and a
    // narrow window gets a taller bar rather than widgets sliced off at the edge.
    public void Layout(int width)
    {
        Width = Math.Max(width, 1);
        float x = PadX, y = PadY;
        bool first = true;
        foreach (var it in _items) it.Rect = default;
        foreach (var it in Shown)
        {
            float w = it.Measure(this);
            if (!first && (it.Break || x + w > Width - PadX)) { x = PadX; y += RowH + RowGap; }
            it.Rect = new RectangleF(x, y, w, RowH);
            x += w + ItemGap;
            first = false;
        }
        Height = (int)(y + RowH + PadY);
    }

    public string StateKey()
        => Width + "x" + (Collapsed ? "c" : "e") + string.Concat(Shown.Select(i => "" + i.StateKey(this)));

    public Image<Rgba32> RenderImage()
    {
        var img = new Image<Rgba32>(Math.Max(Width, 1), Math.Max(Height, 1));
        img.Mutate(ctx =>
        {
            PaintRect(ctx, new RectangleF(0, 0, Width, Height), UiTheme.Panel);
            foreach (var it in Shown) it.Paint(this, ctx);
            PaintRect(ctx, new RectangleF(0, Height - 1, Width, 1), UiTheme.PanelEdge);
        });
        return img;
    }

    // ---- input --------------------------------------------------------------------------
    UiItem? HitTest(float x, float y) => Shown.FirstOrDefault(i => x >= i.Rect.X && x < i.Rect.Right
                                                               && y >= i.Rect.Y && y < i.Rect.Bottom);

    /// <summary>LMB down at panel-space x,y. True when the bar took the click — the loop then
    /// leaves the world alone, so clicking a widget cannot also lock a pick behind it.</summary>
    public bool Press(float x, float y)
    {
        bool inside = Contains(x, y);
        var it = inside ? HitTest(x, y) : null;
        if (Editing is not null && Editing != it) CommitEdit();
        if (it is null) return inside;                     // a click on bare panel is still ours
        _grab = it.OnPress(this, x, y) ? it : null;
        return true;
    }

    public void Drag(float x, float y) => _grab?.OnDrag(this, x, y);

    /// <summary>The wheel, at panel-space x,y. True when the bar took it — which it does for the
    /// whole panel, not just for the sliders: before this, spinning the wheel while the cursor
    /// rested on the clip slider silently changed the FLY SPEED, because that is what the wheel
    /// had always meant and there had never been a slider to rest it on.</summary>
    public bool Scroll(float x, float y, float ticks)
    {
        if (!Contains(x, y)) return false;
        if (ticks != 0f && HitTest(x, y) is UiNumber n && n.Live)
        {
            float cur = n.Get() ?? n.Seed?.Invoke() ?? n.FromNorm(0.5f);
            n.Set(Math.Clamp(n.ScrollStep > 0f ? cur + ticks * n.ScrollStep
                                               : cur * MathF.Pow(1.25f, ticks), n.Min, n.Max));
        }
        return true;
    }

    public void Release()
    {
        if (_grab is { } g) { g.OnRelease(this); _grab = null; }
    }

    public bool Dragging => _grab is not null;

    // ---- the number box's text edit ------------------------------------------------------
    public void BeginEdit(UiNumber n)
    {
        Editing = n;
        EditBad = false;
        EditText = n.Get() is { } v ? v.ToString(n.Format, CultureInfo.InvariantCulture) : "";
    }

    /// <summary>Characters from the window's TextInput event, filtered to what a number can hold.
    /// Reading the event rather than polling key codes is what makes the keypad, a shifted
    /// hyphen, and a non-US layout all type the character the person actually pressed.</summary>
    public void Type(string s)
    {
        if (Editing is null) return;
        foreach (var ch in s)
            if (char.IsAsciiDigit(ch) || ch == '.' || ch == '-') EditText += ch;
        Revalidate();
    }

    public void Backspace()
    {
        if (Editing is null || EditText.Length == 0) return;
        EditText = EditText[..^1];
        Revalidate();
    }

    void Revalidate() => EditBad = EditText.Length > 0 && !TryNum(EditText, out _);

    static bool TryNum(string s, out float v)
        => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public void CommitEdit()
    {
        if (Editing is not { } n) return;
        string t = EditText.Trim();
        if (t.Length == 0) { if (n.Nullable) n.Set(null); }
        else if (TryNum(t, out var v)) n.Set(Math.Clamp(v, n.Min, n.Max));
        else Report($"'{t}' is not a number — {n.Label} left at {(n.Get() is { } cur ? cur.ToString(n.Format, CultureInfo.InvariantCulture) : "off")}.");
        Editing = null; EditText = ""; EditBad = false;
        EchoValue(n);
    }

    public void CancelEdit() { Editing = null; EditText = ""; EditBad = false; }

    /// <summary>The widgets currently on the bar, in layout order (pinned only when collapsed).</summary>
    public IEnumerable<UiItem> Items() => Shown;

    /// <summary>The numeric controls currently on the bar, in layout order.</summary>
    public IEnumerable<UiNumber> Numbers() => Shown.OfType<UiNumber>();

    /// <summary>Tab walks to the next number box, so a whole row can be set without the mouse.</summary>
    public void FocusNext()
    {
        var nums = Numbers().Where(n => n.Live).ToList();
        if (nums.Count == 0) return;
        int at = Editing is null ? -1 : nums.IndexOf(Editing);
        CommitEdit();
        BeginEdit(nums[(at + 1) % nums.Count]);
    }

    // ---- GL -----------------------------------------------------------------------------
    /// <summary>Re-rasterise and re-upload only when something visible actually changed. Must run
    /// on the GL thread.</summary>
    public void Sync(int width)
    {
        Layout(width);
        string key = StateKey();
        if (key == _builtKey && width == _builtW && Height == _builtH && _tex != 0) return;
        _builtKey = key; _builtW = width; _builtH = Height;
        using var img = RenderImage();
        var px = new byte[img.Width * img.Height * 4];
        img.CopyPixelDataTo(px);
        if (_tex == 0)
        {
            _tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _tex);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        }
        else GL.BindTexture(TextureTarget.Texture2D, _tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, img.Width, img.Height, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, px);
    }

    public int Texture => _tex;

    public void Dispose()
    {
        if (_tex != 0) { GL.DeleteTexture(_tex); _tex = 0; }
        foreach (var im in _rasters.Values) im.Dispose();
        _rasters.Clear();
    }
}

/// <summary>A screen-space textured quad, in normalised device coordinates. One program shared by
/// the control bar, the HUD caption and the help card — three overlays that differ only in where
/// they sit and which texture they carry.</summary>
sealed class OverlayQuad : IDisposable
{
    readonly int _prog, _vao, _uRect, _uTex;

    public OverlayQuad()
    {
        const string vs = @"#version 330 core
out vec2 uv;
uniform vec4 u_rect;      // NDC x0, y0(bottom), x1, y1(top)
void main(){
    bool right = (gl_VertexID & 1) == 1;
    bool top = gl_VertexID >= 2;
    uv = vec2(right ? 1.0 : 0.0, top ? 0.0 : 1.0);
    gl_Position = vec4(right ? u_rect.z : u_rect.x, top ? u_rect.w : u_rect.y, 0.0, 1.0);
}";
        const string fs = @"#version 330 core
in vec2 uv;
uniform sampler2D u_tex;
out vec4 frag;
void main(){ frag = texture(u_tex, uv); }";
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v); Check(v, "vertex");
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f); Check(f, "fragment");
        _prog = GL.CreateProgram();
        GL.AttachShader(_prog, v); GL.AttachShader(_prog, f); GL.LinkProgram(_prog);
        GL.GetProgram(_prog, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("overlay shader link failed: " + GL.GetProgramInfoLog(_prog));
        GL.DeleteShader(v); GL.DeleteShader(f);
        _uRect = GL.GetUniformLocation(_prog, "u_rect");
        _uTex = GL.GetUniformLocation(_prog, "u_tex");
        _vao = GL.GenVertexArray();                       // an attribute-less draw still needs one

        static void Check(int s, string what)
        {
            GL.GetShader(s, ShaderParameter.CompileStatus, out int okc);
            if (okc == 0) throw new InvalidOperationException($"overlay {what} shader: {GL.GetShaderInfoLog(s)}");
        }
    }

    public void Begin()
    {
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.UseProgram(_prog);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(_uTex, 0);
        GL.BindVertexArray(_vao);
    }

    /// <summary>Draw a texture into a pixel rect of a framebuffer, y measured DOWN from the top —
    /// the space every layout in this file is expressed in.</summary>
    public void DrawPixels(int tex, float x, float y, float w, float h, int fbW, int fbH)
    {
        if (tex == 0 || w <= 0 || h <= 0 || fbW <= 0 || fbH <= 0) return;
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.Uniform4(_uRect, 2f * x / fbW - 1f, 1f - 2f * (y + h) / fbH,
                            2f * (x + w) / fbW - 1f, 1f - 2f * y / fbH);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    public void End()
    {
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
    }

    public void Dispose()
    {
        GL.DeleteProgram(_prog);
        GL.DeleteVertexArray(_vao);
    }
}

/// <summary>The in-window help sheet — a centred card of heading/body pairs. This is what H now
/// does. It used to print the same text to the console, which is why "H does nothing" was a fair
/// description of it from inside the window; the console line is still printed, because that is
/// where a value gets copied from.</summary>
sealed class HelpCard : IDisposable
{
    readonly Font? _font, _bold;
    readonly TextOptions? _to, _toBold;
    int _tex;
    string _built = "";
    public int Width { get; private set; }
    public int Height { get; private set; }

    const float FontPx = 17f, LineH = 25f, PadX = 20f, PadY = 16f, HeadW = 78f;

    public HelpCard()
    {
        FontFamily fam = default;
        bool got = false;
        foreach (var name in new[] { "Segoe UI", "Arial", "Verdana" })
            if (SystemFonts.TryGet(name, out fam)) { got = true; break; }
        if (!got) foreach (var f in SystemFonts.Families) { fam = f; got = true; break; }
        if (!got) return;
        _font = fam.CreateFont(FontPx, FontStyle.Regular);
        _bold = fam.CreateFont(FontPx, FontStyle.Bold);
        _to = new TextOptions(_font);
        _toBold = new TextOptions(_bold);
    }

    float W(string s, TextOptions? to) => to is null ? s.Length * FontPx * 0.5f : TextMeasurer.MeasureAdvance(s, to).Width;

    /// <summary>Wrap on the real advance width so the card fits the window rather than the other
    /// way round — the help lists live VALUES, and those get long.</summary>
    List<string> Wrap(string text, float max)
    {
        var outp = new List<string>();
        var cur = "";
        foreach (var word in text.Split(' '))
        {
            var probe = cur.Length == 0 ? word : cur + " " + word;
            if (W(probe, _to) <= max) { cur = probe; continue; }
            if (cur.Length > 0) outp.Add(cur);
            cur = word;
        }
        if (cur.Length > 0) outp.Add(cur);
        return outp.Count > 0 ? outp : new List<string> { "" };
    }

    /// <summary>Rasterise the card. Separate from the upload so the layout can be checked
    /// without a GL context — the same split the control bar has, and for the same reason.</summary>
    public Image<Rgba32> RenderImage(IReadOnlyList<(string Head, string Body)> lines, int fbW, int fbH)
    {
        int cardW = Math.Clamp((int)(fbW * 0.72f), 520, 1180);
        float bodyMax = cardW - PadX * 2 - HeadW;
        var laid = lines.Select(l => (l.Head, Body: Wrap(l.Body, bodyMax))).ToList();
        int cardH = (int)(PadY * 2 + laid.Sum(l => l.Body.Count) * LineH);
        Width = cardW; Height = Math.Min(cardH, Math.Max(fbH - 40, 80));

        var img = new Image<Rgba32>(Width, Height);
        img.Mutate(ctx =>
        {
            ctx.Fill(Color.FromRgba(10, 12, 17, 244), new RectangularPolygon(0, 0, Width, Height));
            ctx.Draw(UiTheme.BorderHot, 2f, new RectangularPolygon(1, 1, Width - 2f, Height - 2f));
            float y = PadY;
            foreach (var (head, body) in laid)
            {
                for (int i = 0; i < body.Count; i++)
                {
                    if (i == 0 && head.Length > 0)
                        ctx.DrawText(head, _bold!, UiTheme.BorderHot, new PointF(PadX, y));
                    ctx.DrawText(body[i], _font!, i == 0 ? UiTheme.Text : UiTheme.TextDim,
                                 new PointF(PadX + HeadW, y));
                    y += LineH;
                }
            }
        });
        return img;
    }

    public void Sync(IReadOnlyList<(string Head, string Body)> lines, int fbW, int fbH)
    {
        if (_font is null) return;
        string key = fbW + "x" + fbH + "" + string.Join("", lines.Select(l => l.Head + "" + l.Body));
        if (key == _built && _tex != 0) return;
        _built = key;
        using var img = RenderImage(lines, fbW, fbH);
        var px = new byte[Width * Height * 4];
        img.CopyPixelDataTo(px);
        if (_tex == 0)
        {
            _tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _tex);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        }
        else GL.BindTexture(TextureTarget.Texture2D, _tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Width, Height, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, px);
    }

    public int Texture => _tex;

    public void Dispose()
    {
        if (_tex != 0) { GL.DeleteTexture(_tex); _tex = 0; }
    }
}
