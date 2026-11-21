using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Workbench;

/// <summary>The 3D view as a plain Win32 child window inside the WPF window (an <see cref="HwndHost"/>), drawn with
/// viewprobe's own GL context made current on this window's DC and presented with <c>SwapBuffers</c>. The child takes
/// the context window's pixel format, so the context (and every mesh and texture the scene build uploaded to it) is
/// valid here. Chosen after <c>GLWpfControl</c> (4.3.3 and 4.3.4) paced every frame at 140 ms on this machine whatever
/// was drawn (stage 2 receipts). Win32 calls used: CreateWindowEx, GetDC, GetPixelFormat, DescribePixelFormat,
/// SetPixelFormat, wglMakeCurrent, wglGetProcAddress("wglSwapIntervalEXT"), SwapBuffers, GetClientRect.</summary>
internal sealed class NativeGlHost : HwndHost
{
    readonly IntPtr _contextWindow;   // the hidden GLFW window whose context the scene was built on
    readonly IntPtr _hglrc;
    IntPtr _hwnd, _hdc;

    internal event Action<int, int, int, IntPtr>? Message;   // (msg, x, y, wParam) for mouse and key messages

    internal NativeGlHost(IntPtr contextWindow, IntPtr hglrc) { _contextWindow = contextWindow; _hglrc = hglrc; }

    internal IntPtr Handle => _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                               0, 0, 64, 64, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"the 3D view's child window could not be created (Win32 error {Marshal.GetLastWin32Error()}).");
        // SS_NOTIFY so a static control reports clicks; the messages are read in WndProc below.
        SetWindowLongPtr(_hwnd, -16, (IntPtr)(GetWindowLongPtr(_hwnd, -16).ToInt64() | 0x0100));
        _hdc = GetDC(_hwnd);
        var srcDc = GetDC(_contextWindow);
        int pf = GetPixelFormat(srcDc);
        var pfd = new PIXELFORMATDESCRIPTOR { nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1 };
        DescribePixelFormat(srcDc, pf, pfd.nSize, ref pfd);
        ReleaseDC(_contextWindow, srcDc);
        if (pf == 0 || !SetPixelFormat(_hdc, pf, ref pfd))
            throw new InvalidOperationException($"the 3D view could not take the scene context's pixel format {pf} (Win32 error {Marshal.GetLastWin32Error()}).");
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (_hdc != IntPtr.Zero) ReleaseDC(_hwnd, _hdc);
        DestroyWindow(hwnd.Handle);
    }

    /// <summary>Make the scene's context current on this window. False when the window does not exist yet.</summary>
    internal bool MakeCurrent()
    {
        if (_hdc == IntPtr.Zero) return false;
        if (!wglMakeCurrent(_hdc, _hglrc))
            throw new InvalidOperationException($"the scene's GL context could not be made current on the 3D view (Win32 error {Marshal.GetLastWin32Error()}).");
        return true;
    }

    internal void Swap() => SwapBuffers(_hdc);

    internal (int W, int H) ClientSize()
    {
        GetClientRect(_hwnd, out var r);
        return (Math.Max(r.Right - r.Left, 1), Math.Max(r.Bottom - r.Top, 1));
    }

    delegate bool SwapIntervalFn(int interval);
    /// <summary>1 = wait for vertical blank, 0 = do not. Needs the context current. False when the driver has no
    /// WGL_EXT_swap_control.</summary>
    internal bool SetSwapInterval(int interval)
    {
        var p = wglGetProcAddress("wglSwapIntervalEXT");
        if (p == IntPtr.Zero) return false;
        return Marshal.GetDelegateForFunctionPointer<SwapIntervalFn>(p)(interval);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is >= 0x200 and <= 0x20A or 0x100 or 0x101 or 0x0008)
        {
            long l = lParam.ToInt64();
            int x = (short)(l & 0xFFFF), y = (short)((l >> 16) & 0xFFFF);
            if (msg == 0x201) { SetFocus(_hwnd); SetCapture(_hwnd); _hoverFocus = IntPtr.Zero; }
            if (msg == 0x202) ReleaseCapture();
            if (msg == 0x200) HoverFocus();
            Message?.Invoke(msg, x, y, wParam);
        }
        if (msg == 0x02A3) HoverLeave();                                       // WM_MOUSELEAVE
        if (msg is 0x000F or 0x0005) Message?.Invoke(msg, 0, 0, IntPtr.Zero);   // WM_PAINT, WM_SIZE: the view needs a frame
        if (msg == 0x0087) { handled = true; return (IntPtr)0x0004; }   // WM_GETDLGCODE: DLGC_WANTALLKEYS
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    // The fly keys answer while the mouse is over the view, as well as after a click: the first move over it takes the
    // keyboard, unless the app is not the active window or a text field is being typed in, and leaving gives the keyboard
    // back to the window it came from. A click keeps the keyboard in the view after the mouse leaves, as before.
    IntPtr _hoverFocus;   // the window the keyboard was taken from by hovering; zero when it was not taken that way
    bool _tracking;

    void HoverFocus()
    {
        if (!_tracking)
        {
            var t = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x0002 /* TME_LEAVE */, hwndTrack = _hwnd };
            _tracking = TrackMouseEvent(ref t);
        }
        if (GetFocus() == _hwnd) return;
        var top = GetAncestor(_hwnd, 2 /* GA_ROOT */);
        if (GetForegroundWindow() != top) return;
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.ComboBox { IsEditable: true, IsKeyboardFocusWithin: true }) return;
        var from = GetFocus();
        SetFocus(_hwnd);
        _hoverFocus = from == IntPtr.Zero ? top : from;
    }

    void HoverLeave()
    {
        _tracking = false;
        if (_hoverFocus == IntPtr.Zero) return;
        var back = _hoverFocus; _hoverFocus = IntPtr.Zero;
        if (GetFocus() == _hwnd) SetFocus(back);   // WM_KILLFOCUS follows: a held fly key stops
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift,
        cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
        cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved; public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int GetPixelFormat(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int DescribePixelFormat(IntPtr hdc, int pf, uint size, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool SetPixelFormat(IntPtr hdc, int pf, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll", SetLastError = true)] static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] static extern IntPtr wglGetProcAddress(string name);
}
