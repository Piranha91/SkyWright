using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Workbench;

/// <summary>A picture of the workbench window as the modder sees it: the WPF content (tabs, banner, status bar) rendered by
/// WPF, with the scene's own frame placed where the view sits. WPF cannot render the native GL child window and Win32
/// PrintWindow returned a blank window here (stage 4, run 1), so the picture is a composite, and is called one.</summary>
internal static class WindowCapture
{
    /// <summary>Save the window's content alone (no scene view: the launcher before a run) as PNG; returns its size in device pixels.</summary>
    internal static (int W, int H) SaveWindow(Window window, string path)
    {
        if (window.Content is not FrameworkElement content || content.ActualWidth <= 0)
            throw new InvalidOperationException("the workbench window has no laid-out content yet, so it cannot be captured.");
        var dpi = VisualTreeHelper.GetDpi(window);
        int w = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);
        var ui = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        ui.Render(dv);                                    // the window's background first, then its content at its own size
        ui.Render(content);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(ui));
        enc.Save(f);
        return (w, h);
    }

    /// <summary>Save the composite as PNG; returns its size in device pixels.</summary>
    internal static (int W, int H) SaveComposite(Window window, FrameworkElement viewSlot, string viewPng, string path)
    {
        if (window.Content is not FrameworkElement content || content.ActualWidth <= 0)
            throw new InvalidOperationException("the workbench window has no laid-out content yet, so it cannot be captured.");
        var dpi = VisualTreeHelper.GetDpi(window);
        int w = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);
        var ui = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        ui.Render(content);
        var view = new BitmapImage();
        view.BeginInit(); view.CacheOption = BitmapCacheOption.OnLoad; view.UriSource = new Uri(Path.GetFullPath(viewPng)); view.EndInit();
        var at = viewSlot.TranslatePoint(new Point(0, 0), content);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // The window's own background first: WPF renders unpainted areas transparent, which a PNG viewer shows black.
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawImage(ui, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawImage(view, new Rect(at, new Size(viewSlot.ActualWidth, viewSlot.ActualHeight)));
        }
        var all = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        all.Render(dv);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(all));
        enc.Save(f);
        return (w, h);
    }
}
