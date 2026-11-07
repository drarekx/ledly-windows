using System.Diagnostics;
using Ledly.Windows.Interop;
using Ledly.Windows.Platform;

namespace Ledly.Windows.Capture;

/// <summary>
/// Fallback when Windows.Graphics.Capture is missing or fails: GDI StretchBlt (HALFTONE) of the
/// monitor into a 256-px-wide DIB section, or PrintWindow of the app's window. Slower and blind to
/// some hardware-accelerated content, but works everywhere.
/// </summary>
sealed class GdiSession : ICaptureSession
{
    const int TargetWidth = 256;

    readonly DisplayMonitor? _monitor;
    readonly IntPtr _window;
    readonly int _fps;
    readonly FrameSink _sink;
    readonly Thread _thread;
    volatile bool _stop;
    volatile bool _broken;

    public bool Broken => _broken;

    GdiSession(DisplayMonitor? monitor, IntPtr window, int fps, FrameSink sink)
    {
        _monitor = monitor;
        _window = window;
        _fps = Math.Clamp(fps, 1, 60);
        _sink = sink;
        _thread = new Thread(Run) { Name = "ledly.capture.gdi", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static GdiSession ForMonitor(DisplayMonitor monitor, int fps, FrameSink sink) => new(monitor, IntPtr.Zero, fps, sink);
    public static GdiSession ForWindow(IntPtr hwnd, int fps, FrameSink sink) => new(null, hwnd, fps, sink);

    void Run()
    {
        var interval = 1000.0 / _fps;
        var clock = Stopwatch.StartNew();
        var dib = new Dib();
        var window = new WindowBuffer();
        try
        {
            while (!_stop)
            {
                var start = clock.Elapsed.TotalMilliseconds;
                try
                {
                    if (_window != IntPtr.Zero) GrabWindow(dib, window);
                    else GrabMonitor(dib);
                }
                catch (Exception e)
                {
                    Log.Write($"capture (gdi): {e.Message}");
                    _broken = true;
                    return;
                }
                var wait = interval - (clock.Elapsed.TotalMilliseconds - start);
                if (wait > 0) Thread.Sleep(TimeSpan.FromMilliseconds(wait));
            }
        }
        finally
        {
            dib.Dispose();
            window.Dispose();
        }
    }

    void GrabMonitor(Dib dib)
    {
        var r = _monitor!.Bounds;
        if (r.Width <= 0 || r.Height <= 0) return;
        var height = Math.Max(16, (int)Math.Round(TargetWidth * (double)r.Height / r.Width));
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
        try
        {
            dib.Ensure(screen, TargetWidth, height);
            Native.SetStretchBltMode(dib.Dc, Native.HALFTONE);
            Native.SetBrushOrgEx(dib.Dc, 0, 0, IntPtr.Zero);
            if (!Native.StretchBlt(dib.Dc, 0, 0, TargetWidth, height, screen, r.Left, r.Top, r.Width, r.Height, Native.SRCCOPY))
                return; // secure desktop (UAC, lock screen): try again next time
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        dib.Deliver(_sink);
    }

    void GrabWindow(Dib dib, WindowBuffer buffer)
    {
        if (!Native.IsWindow(_window))
        {
            _broken = true;
            return;
        }
        Native.GetWindowRect(_window, out var r);
        if (r.Width <= 0 || r.Height <= 0) return;
        var height = Math.Max(16, (int)Math.Round(TargetWidth * (double)r.Height / r.Width));
        var screen = Native.GetDC(IntPtr.Zero);
        try
        {
            buffer.Ensure(screen, r.Width, r.Height);
            dib.Ensure(screen, TargetWidth, height);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        if (!Native.PrintWindow(_window, buffer.Dc, Native.PW_RENDERFULLCONTENT)) return;
        Native.SetStretchBltMode(dib.Dc, Native.HALFTONE);
        Native.SetBrushOrgEx(dib.Dc, 0, 0, IntPtr.Zero);
        Native.StretchBlt(dib.Dc, 0, 0, TargetWidth, height, buffer.Dc, 0, 0, r.Width, r.Height, Native.SRCCOPY);
        dib.Deliver(_sink);
    }

    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
    }

    /// <summary>Top-down 32-bit DIB section the small frame is drawn into.</summary>
    sealed class Dib : IDisposable
    {
        public IntPtr Dc;
        IntPtr _bitmap, _old, _bits;
        int _width, _height;

        public void Ensure(IntPtr reference, int width, int height)
        {
            if (Dc != IntPtr.Zero && width == _width && height == _height) return;
            Dispose();
            Dc = Native.CreateCompatibleDC(reference);
            var header = new BITMAPINFOHEADER
            {
                biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            _bitmap = Native.CreateDIBSection(Dc, ref header, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            _old = Native.SelectObject(Dc, _bitmap);
            _width = width;
            _height = height;
        }

        public unsafe void Deliver(FrameSink sink)
        {
            Native.GdiFlush();
            var stride = _width * 4;
            var span = new Span<byte>((void*)_bits, stride * _height);
            // GDI leaves alpha at 0.
            for (var i = 3; i < span.Length; i += 4) span[i] = 255;
            sink(span, _width, _height, stride);
        }

        public void Dispose()
        {
            if (Dc != IntPtr.Zero)
            {
                if (_old != IntPtr.Zero) Native.SelectObject(Dc, _old);
                Native.DeleteDC(Dc);
            }
            if (_bitmap != IntPtr.Zero) Native.DeleteObject(_bitmap);
            Dc = _bitmap = _old = _bits = IntPtr.Zero;
            _width = _height = 0;
        }
    }

    /// <summary>Full-size bitmap PrintWindow draws the window into.</summary>
    sealed class WindowBuffer : IDisposable
    {
        public IntPtr Dc;
        IntPtr _bitmap, _old;
        int _width, _height;

        public void Ensure(IntPtr reference, int width, int height)
        {
            if (Dc != IntPtr.Zero && width == _width && height == _height) return;
            Dispose();
            Dc = Native.CreateCompatibleDC(reference);
            _bitmap = Native.CreateCompatibleBitmap(reference, width, height);
            if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleBitmap failed");
            _old = Native.SelectObject(Dc, _bitmap);
            _width = width;
            _height = height;
        }

        public void Dispose()
        {
            if (Dc != IntPtr.Zero)
            {
                if (_old != IntPtr.Zero) Native.SelectObject(Dc, _old);
                Native.DeleteDC(Dc);
            }
            if (_bitmap != IntPtr.Zero) Native.DeleteObject(_bitmap);
            Dc = _bitmap = _old = IntPtr.Zero;
            _width = _height = 0;
        }
    }
}
