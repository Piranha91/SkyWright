using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Workbench;

/// <summary>A picture of the whole workbench window as the modder sees it (tabs, banner, status bar and the 3D view), by
/// Win32 PrintWindow with full content, so the GL child window is in it too. A scene capture is the 3D view alone.</summary>
internal static class WindowCapture
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    const uint PW_RENDERFULLCONTENT = 2;

    /// <summary>Save the window as PNG; returns its size in device pixels, or throws one sentence.</summary>
    internal static (int W, int H) Save(Window window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("the workbench window has no handle yet, so it cannot be captured.");
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bmp);
        try
        {
            if (!PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)) throw new InvalidOperationException("Windows refused to print the workbench window (PrintWindow failed).");
            SelectObject(dc, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var f = File.Create(path);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            enc.Save(f);
            return (w, h);
        }
        finally { DeleteObject(bmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
    }
}
