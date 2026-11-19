using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Workbench;

/// <summary>The World tab's map: one worldspace's terrain drawn from above as shaded relief, from the load order's own
/// LAND records (<see cref="WorldIndex.TerrainOfAsync"/>). The wheel zooms about the pointer, a drag pans, the pointer
/// names the cell under it and a click picks that cell. North is up. Nothing here edits.</summary>
internal sealed class WorldMap
{
    readonly Border _frame = new() { ClipToBounds = true, Background = Theme.Brush("#101214"), MinHeight = 120, Cursor = Cursors.Cross };
    readonly Canvas _sheet = new();                         // bitmap pixels; the pan and zoom transform is on it
    readonly Image _image = new();
    readonly Rectangle _hover = Box("#FFFFFF"), _chosen = Box("#00E5FF");
    readonly MatrixTransform _view = new();
    WorldTerrain? _terrain;
    Point _down;
    bool _pressed, _dragged;

    /// <summary>The grid square under the pointer changed; null when the pointer left the map.</summary>
    internal event Action<(int X, int Y)?>? Hovered;
    /// <summary>A grid square was clicked.</summary>
    internal event Action<int, int>? Clicked;

    internal UIElement View => _frame;
    internal WorldTerrain? Terrain => _terrain;
    internal (int X, int Y)? Chosen { get; private set; }

    static Rectangle Box(string hex) => new() { Stroke = Theme.Brush(hex), Fill = Brushes.Transparent, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

    internal WorldMap()
    {
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
        _sheet.RenderTransform = _view;
        _sheet.Children.Add(_image); _sheet.Children.Add(_chosen); _sheet.Children.Add(_hover);
        _frame.Child = _sheet;
        _frame.SizeChanged += (_, e) => { if (e.PreviousSize.Width < 1 || e.PreviousSize.Height < 1) Fit(); };
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
        }
    }

    void Fit()
    {
        if (_terrain is not { } t || _frame.ActualWidth < 1 || _frame.ActualHeight < 1) return;
        double w = t.Width * t.Samples, h = t.Height * t.Samples;
        double s = Math.Min(_frame.ActualWidth / w, _frame.ActualHeight / h);
        _view.Matrix = new Matrix(s, 0, 0, s, (_frame.ActualWidth - w * s) / 2, (_frame.ActualHeight - h * s) / 2);
        Strokes();
    }

    void Zoom(Point about, double by)
    {
        if (_terrain is null) return;
        var m = _view.Matrix;
        double next = m.M11 * by;
        if (next < 0.02 || next > 64) return;
        m.ScaleAt(by, by, about.X, about.Y);
        _view.Matrix = m;
        Strokes();
    }

    // the outlines stay about a pixel and a half wide on screen at any zoom
    void Strokes() => _hover.StrokeThickness = _chosen.StrokeThickness = 1.5 / Math.Max(_view.Matrix.M11, 1e-6);

    void Moved(Point p)
    {
        if (_pressed && (_dragged || Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 4))
        {
            var m = _view.Matrix;
            m.Translate(p.X - _down.X, p.Y - _down.Y);
            _view.Matrix = m; _down = p; _dragged = true;
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
