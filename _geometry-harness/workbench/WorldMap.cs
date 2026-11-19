using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Workbench;

/// <summary>The World tab's map: one worldspace's terrain drawn from above as shaded relief, from the load order's own
/// LAND records (<see cref="WorldIndex.TerrainOfAsync"/>). The wheel zooms about the pointer, a drag pans, the pointer
/// names the cell under it and a click picks that cell. Over the terrain it draws each building's outline and the
/// game's own map markers as captions: hold capitals at any zoom, towns and forts closer in, everything else closer
/// still, a caption giving way to a more important one it would cover. North is up. Nothing here edits.</summary>
internal sealed class WorldMap
{
    readonly Border _frame = new() { ClipToBounds = true, Background = Theme.Brush("#101214"), MinHeight = 120, Cursor = Cursors.Cross };
    readonly Canvas _sheet = new();                         // bitmap pixels; the pan and zoom transform is on it
    readonly Image _image = new();
    readonly Rectangle _hover = Box("#FFFFFF"), _chosen = Box("#00E5FF");
    readonly System.Windows.Shapes.Path _buildings = new() { Fill = Theme.Brush("#D8E6DCCB"), Stroke = Theme.Brush("#2A2622"), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Canvas _captions = new() { IsHitTestVisible = false };   // screen pixels, over the sheet: text keeps its size at any zoom
    readonly MatrixTransform _view = new();
    readonly List<(MapPlace Place, int Tier, Border Box, TextBlock Text, System.Windows.Shapes.Ellipse Dot)> _places = new();
    WorldTerrain? _terrain;
    WorldFeatures? _features;
    Point _down;
    bool _pressed, _dragged;

    /// <summary>The grid square under the pointer changed; null when the pointer left the map.</summary>
    internal event Action<(int X, int Y)?>? Hovered;
    /// <summary>A grid square was clicked.</summary>
    internal event Action<int, int>? Clicked;

    internal UIElement View => _frame;
    internal WorldTerrain? Terrain => _terrain;
    internal (int X, int Y)? Chosen { get; private set; }
    internal WorldFeatures? Features => _features;
    /// <summary>The captions drawn now, most important first.</summary>
    internal IReadOnlyList<string> CaptionsShown { get; private set; } = Array.Empty<string>();
    internal bool BuildingsShown => _buildings.Visibility == Visibility.Visible;
    /// <summary>Screen pixels across one cell at the current zoom.</summary>
    internal double PixelsPerCell => _terrain is { } t ? _view.Matrix.M11 * t.Samples : 0;

    static Rectangle Box(string hex) => new() { Stroke = Theme.Brush(hex), Fill = Brushes.Transparent, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

    internal WorldMap()
    {
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
        _sheet.RenderTransform = _view;
        _sheet.Children.Add(_image); _sheet.Children.Add(_buildings); _sheet.Children.Add(_chosen); _sheet.Children.Add(_hover);
        var layers = new Grid();
        layers.Children.Add(_sheet); layers.Children.Add(_captions);
        _frame.Child = layers;
        _frame.SizeChanged += (_, e) => { if (e.PreviousSize.Width < 1 || e.PreviousSize.Height < 1) Fit(); else Viewed(); };
        _frame.MouseWheel += (_, e) => Zoom(e.GetPosition(_frame), e.Delta > 0 ? 1.25 : 0.8);
        _frame.MouseLeftButtonDown += (_, e) => { _down = e.GetPosition(_frame); _pressed = true; _dragged = false; _frame.CaptureMouse(); };
        _frame.MouseMove += (_, e) => Moved(e.GetPosition(_frame));
        _frame.MouseLeftButtonUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false; _frame.ReleaseMouseCapture();
            if (!_dragged && SquareAt(e.GetPosition(_frame)) is { } at) Clicked?.Invoke(at.X, at.Y);
        };
        _frame.MouseLeave += (_, _) => { _hover.Visibility = Visibility.Collapsed; Hovered?.Invoke(null); };
    }

    /// <summary>Show a worldspace's terrain (null clears the map), fitted to the panel.</summary>
    internal void Show(WorldTerrain? t, BitmapSource? picture)
    {
        _terrain = t; _image.Source = picture; Chosen = null;
        ShowFeatures(null);
        _hover.Visibility = _chosen.Visibility = Visibility.Collapsed;
        Fit();
    }

    /// <summary>Mark a grid square as the chosen cell, and bring it into the panel when it is outside.</summary>
    internal void Choose(int x, int y)
    {
        if (_terrain is not { } t) return;
        Chosen = (x, y);
        Place(_chosen, x, y);
        var c = _view.Matrix.Transform(new Point((x - t.MinX + 0.5) * t.Samples, (t.MaxY - y + 0.5) * t.Samples));
        if (c.X < 0 || c.Y < 0 || c.X > _frame.ActualWidth || c.Y > _frame.ActualHeight)
        {
            var m = _view.Matrix;
            m.Translate(_frame.ActualWidth / 2 - c.X, _frame.ActualHeight / 2 - c.Y);
            _view.Matrix = m;
            Viewed();
        }
    }

    /// <summary>Draw a worldspace's buildings and captions over the terrain shown (null clears them).</summary>
    internal void ShowFeatures(WorldFeatures? f)
    {
        _features = f; _places.Clear(); _captions.Children.Clear(); _buildings.Data = null; CaptionsShown = Array.Empty<string>();
        if (f is null || _terrain is not { } t) { _buildings.Visibility = Visibility.Collapsed; return; }
        // world units to sheet pixels: one cell is 4096 units and Samples pixels; north is up
        double k = t.Samples / 4096.0, x0 = t.MinX * 4096.0, yTop = (t.MaxY + 1) * 4096.0;
        var g = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var c = g.Open())
            foreach (var ring in f.Outlines)
            {
                if (ring.Length < 6) continue;
                c.BeginFigure(new Point((ring[0] - x0) * k, (yTop - ring[1]) * k), true, true);
                for (int i = 2; i < ring.Length; i += 2) c.LineTo(new Point((ring[i] - x0) * k, (yTop - ring[i + 1]) * k), true, false);
            }
        g.Freeze();
        _buildings.Data = g;
        foreach (var place in f.Places.OrderBy(p => TierOf(p.Type)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            int tier = TierOf(place.Type);
            var text = new TextBlock { Text = place.Name, Foreground = Brushes.White, FontWeight = tier == 0 ? FontWeights.SemiBold : FontWeights.Normal };
            var box = new Border { Child = text, Background = Theme.Brush("#B8101214"), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Visibility = Visibility.Collapsed };
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1, Visibility = Visibility.Collapsed };
            _captions.Children.Add(dot); _captions.Children.Add(box);
            _places.Add((place, tier, box, text, dot));
        }
        Viewed();
    }

    /// <summary>How soon a marker's caption appears: 0 a city or hold capital (always), 1 a town, settlement, castle,
    /// fort or stronghold (zoomed part of the way in), 2 everything else (zoomed well in). From the marker's own type.</summary>
    internal static int TierOf(string type) =>
        type == "City" || type.EndsWith("Capitol", StringComparison.Ordinal) ? 0
        : type is "Town" or "Settlement" or "Fort" or "OrcStronghold" or "RavenRock" or "TelMithryn" or "TempleOfMiraak" || type.EndsWith("Castle", StringComparison.Ordinal) || type.StartsWith("Castle", StringComparison.Ordinal) ? 1
        : 2;

    /// <summary>Screen pixels across one cell at which each tier's captions appear, and at which buildings do.</summary>
    internal static readonly double[] TierAppears = { 0, 9, 26 };
    internal const double BuildingsAppear = 12;

    /// <summary>The view moved or zoomed: outlines keep their screen width, buildings show when close enough, and the
    /// captions are placed again, the more important first; one that would cover a caption already placed is left out.</summary>
    void Viewed()
    {
        double scale = Math.Max(_view.Matrix.M11, 1e-6);
        _hover.StrokeThickness = _chosen.StrokeThickness = 1.5 / scale;
        if (_terrain is not { } t) return;
        double perCell = PixelsPerCell;
        _buildings.Visibility = _features is not null && perCell >= BuildingsAppear ? Visibility.Visible : Visibility.Collapsed;
        _buildings.StrokeThickness = perCell >= 48 ? 0.8 / scale : 0;
        double k = t.Samples / 4096.0, x0 = t.MinX * 4096.0, yTop = (t.MaxY + 1) * 4096.0;
        double w = _frame.ActualWidth, h = _frame.ActualHeight;
        var taken = new List<Rect>();
        var shown = new List<string>();
        foreach (var (place, tier, box, text, dot) in _places)
        {
            box.Visibility = dot.Visibility = Visibility.Collapsed;
            if (perCell < TierAppears[tier]) continue;
            var at = _view.Matrix.Transform(new Point((place.X - x0) * k, (yTop - place.Y) * k));
            if (at.X < -40 || at.Y < -20 || at.X > w + 40 || at.Y > h + 20) continue;
            // a caption grows a little as the view closes in on it, up to four points over its base size
            double size = (tier == 0 ? 14 : tier == 1 ? 12.5 : 11) + Math.Clamp(1.5 * Math.Log2(Math.Max(perCell, 1) / Math.Max(TierAppears[tier], 4)), 0, 4);
            text.FontSize = size;
            // the text is measured, not its box: a collapsed box measures as nothing
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var want = new Size(text.DesiredSize.Width + 8, text.DesiredSize.Height + 1);
            // above the place, or else below it, beside it, or off one of its corners: the first that covers no caption already placed
            Rect? free = null;
            double ww = want.Width, hh = want.Height;
            foreach (var spot in new[]
                     {
                         new Rect(at.X - ww / 2, at.Y - hh - 5, ww, hh), new Rect(at.X - ww / 2, at.Y + 5, ww, hh),
                         new Rect(at.X + 6, at.Y - hh / 2, ww, hh), new Rect(at.X - ww - 6, at.Y - hh / 2, ww, hh),
                         new Rect(at.X - ww - 3, at.Y - hh - 3, ww, hh), new Rect(at.X + 3, at.Y - hh - 3, ww, hh),
                         new Rect(at.X - ww - 3, at.Y + 3, ww, hh), new Rect(at.X + 3, at.Y + 3, ww, hh),
                     })
            {
                var guard = Rect.Inflate(spot, 3, 2);
                if (taken.Any(r => r.IntersectsWith(guard))) continue;
                free = spot; break;
            }
            if (free is not { } rect) continue;
            taken.Add(rect);
            Canvas.SetLeft(box, rect.X); Canvas.SetTop(box, rect.Y);
            Canvas.SetLeft(dot, at.X - 2.5); Canvas.SetTop(dot, at.Y - 2.5);
            box.Visibility = dot.Visibility = Visibility.Visible;
            shown.Add(place.Name);
        }
        CaptionsShown = shown;
    }

    void Fit()
    {
        if (_terrain is not { } t || _frame.ActualWidth < 1 || _frame.ActualHeight < 1) return;
        double w = t.Width * t.Samples, h = t.Height * t.Samples;
        double s = Math.Min(_frame.ActualWidth / w, _frame.ActualHeight / h);
        _view.Matrix = new Matrix(s, 0, 0, s, (_frame.ActualWidth - w * s) / 2, (_frame.ActualHeight - h * s) / 2);
        Viewed();
    }

    void Zoom(Point about, double by)
    {
        if (_terrain is null) return;
        var m = _view.Matrix;
        double next = m.M11 * by;
        if (next < 0.02 || next > 64) return;
        m.ScaleAt(by, by, about.X, about.Y);
        _view.Matrix = m;
        Viewed();
    }

    void Moved(Point p)
    {
        if (_pressed && (_dragged || Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 4))
        {
            var m = _view.Matrix;
            m.Translate(p.X - _down.X, p.Y - _down.Y);
            _view.Matrix = m; _down = p; _dragged = true;
            Viewed();
            return;
        }
        var at = SquareAt(p);
        if (at is { } sq) Place(_hover, sq.X, sq.Y); else _hover.Visibility = Visibility.Collapsed;
        Hovered?.Invoke(at);
    }

    (int X, int Y)? SquareAt(Point p)
    {
        if (_terrain is not { } t || !_view.Matrix.HasInverse) return null;
        var m = _view.Matrix; m.Invert();
        var q = m.Transform(p);
        int cx = (int)Math.Floor(q.X / t.Samples), cy = (int)Math.Floor(q.Y / t.Samples);
        return cx < 0 || cy < 0 || cx >= t.Width || cy >= t.Height ? null : (t.MinX + cx, t.MaxY - cy);
    }

    void Place(Rectangle r, int x, int y)
    {
        if (_terrain is not { } t) return;
        Canvas.SetLeft(r, (x - t.MinX) * t.Samples); Canvas.SetTop(r, (t.MaxY - y) * t.Samples);
        r.Width = r.Height = t.Samples;
        r.Visibility = Visibility.Visible;
    }

    /// <summary>The terrain as a picture, one pixel per sample: height sets the colour (low green-grey to high white)
    /// and a light from the north-west shades the slopes; ground below the worldspace's default water level is blue,
    /// darker with depth; ground lent by the parent worldspace is dimmed. A square with a cell and no heights is dark grey; a square
    /// with no cell is left black. Safe on a background thread; the picture is frozen.</summary>
    internal static BitmapSource Paint(WorldTerrain t)
    {
        int w = t.Width * t.Samples, h = t.Height * t.Samples;
        float lo = float.MaxValue, hi = float.MinValue;
        float water = t.WaterHeight ?? float.MinValue;
        // the colour scale runs over the ground above water, so a deep sea floor does not flatten the land's colours
        foreach (var v in t.Heights) if (!float.IsNaN(v) && v >= water) { lo = MathF.Min(lo, v); hi = MathF.Max(hi, v); }
        float span = hi > lo ? hi - lo : 1f;
        float spacing = 4096f / t.Samples;                   // world units between samples
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                float v = t.Heights[y * w + x];
                if (float.IsNaN(v))
                {
                    byte g = t.Cells[y / t.Samples * t.Width + x / t.Samples] ? (byte)0x2A : (byte)0x00;
                    px[o] = px[o + 1] = px[o + 2] = g; px[o + 3] = 255;
                    continue;
                }
                float At(int ax, int ay) => ax < 0 || ay < 0 || ax >= w || ay >= h || float.IsNaN(t.Heights[ay * w + ax]) ? v : t.Heights[ay * w + ax];
                // the slope east and south, lit from the north-west and above
                float dx = (At(x + 1, y) - At(x - 1, y)) / (2 * spacing), dy = (At(x, y + 1) - At(x, y - 1)) / (2 * spacing);
                float shade = Math.Clamp(0.72f + 0.9f * (-dx * -0.707f + -dy * -0.707f) / MathF.Sqrt(1 + dx * dx + dy * dy), 0.25f, 1.25f);
                var (r, g2, b) = Ramp((v - lo) / span);
                if (v < water)
                {
                    float deep = Math.Clamp((water - v) / 3000f, 0f, 1f);
                    (r, g2, b) = (58 - 26 * deep, 96 - 40 * deep, 134 - 44 * deep);
                    shade = 1f + (shade - 0.72f) * 0.35f;       // the sea floor's relief shows faintly
                }
                if (t.Borrowed.Length > 0 && t.Borrowed[y / t.Samples * t.Width + x / t.Samples])
                {
                    float grey = (r + g2 + b) / 3f;             // the parent's ground: paler in colour and darker
                    (r, g2, b) = ((r + grey) * 0.5f, (g2 + grey) * 0.5f, (b + grey) * 0.5f);
                    shade *= 0.68f;
                }
                px[o] = (byte)Math.Clamp(b * shade, 0, 255); px[o + 1] = (byte)Math.Clamp(g2 * shade, 0, 255); px[o + 2] = (byte)Math.Clamp(r * shade, 0, 255); px[o + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    static readonly (float At, float R, float G, float B)[] Stops =
    {
        (0.00f, 52, 70, 84), (0.12f, 74, 104, 78), (0.35f, 120, 132, 88), (0.60f, 140, 124, 104), (0.80f, 176, 172, 168), (1.00f, 244, 246, 250),
    };

    static (float R, float G, float B) Ramp(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        for (int i = 1; i < Stops.Length; i++)
            if (f <= Stops[i].At)
            {
                var (a, b) = (Stops[i - 1], Stops[i]);
                float k = (f - a.At) / (b.At - a.At);
                return (a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);
            }
        return (Stops[^1].R, Stops[^1].G, Stops[^1].B);
    }
}
