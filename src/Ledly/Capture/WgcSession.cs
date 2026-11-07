using System.Diagnostics;
using Ledly.Windows.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Ledly.Windows.Capture;

/// <summary>Delivers one small BGRA frame: pixels (valid only during the call), width, height, stride.</summary>
delegate void FrameSink(ReadOnlySpan<byte> pixels, int width, int height, int stride);

/// <summary>A capture source that runs until disposed or until it fails / its target goes away.</summary>
interface ICaptureSession : IDisposable
{
    /// <summary>The window or display went away, or the GPU device was lost: start again.</summary>
    bool Broken { get; }
}

/// <summary>Shared Direct3D 11 device for Windows.Graphics.Capture; recreated after a device loss.</summary>
sealed class D3DDevice : IDisposable
{
    public IntPtr Device;
    public IntPtr Context;
    public IDirect3DDevice WinRT { get; }

    public D3DDevice()
    {
        (Device, Context) = D3D11.CreateDevice();
        try
        {
            WinRT = WinRTInterop.CreateDirect3DDevice(Device);
        }
        catch
        {
            D3D11.Release(ref Context);
            D3D11.Release(ref Device);
            throw;
        }
    }

    public bool Lost => D3D11.GetDeviceRemovedReason(Device) != 0;

    public void Dispose()
    {
        WinRT.Dispose();
        D3D11.Release(ref Context);
        D3D11.Release(ref Device);
    }
}

/// <summary>
/// Windows.Graphics.Capture of a monitor or a window. Each delivered frame is copied into a texture
/// with mipmaps, reduced on the GPU (GenerateMips) and the level closest to 256 px wide is read back
/// through a small staging texture, so the CPU only sees a few hundred KB per frame.
/// </summary>
sealed class WgcSession : ICaptureSession
{
    const int TargetWidth = 256;
    const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    static bool _askedBorderless;

    readonly D3DDevice _device;
    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly FrameSink _sink;
    readonly long _interval;
    readonly object _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _last = long.MinValue;
    SizeInt32 _poolSize;
    bool _disposed;
    volatile bool _broken;

    // GPU resources for the reduction, recreated when the content size changes.
    IntPtr _mipTexture, _mipView, _staging;
    uint _mipWidth, _mipHeight, _level, _levelWidth, _levelHeight;

    public bool Broken => _broken || _device.Lost;

    public WgcSession(D3DDevice device, GraphicsCaptureItem item, int fps, FrameSink sink)
    {
        _device = device;
        _item = item;
        _sink = sink;
        _interval = Stopwatch.Frequency / Math.Clamp(fps, 1, 120);
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.WinRT, Format, 2, _poolSize);
        try
        {
            _session = _pool.CreateCaptureSession(item);
            ConfigureSession(_session);
            _item.Closed += OnItemClosed;
            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
        }
        catch
        {
            _item.Closed -= OnItemClosed;
            _pool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _pool.Dispose();
            throw;
        }
    }

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch { return false; }
        }
    }

    static void ConfigureSession(GraphicsCaptureSession session)
    {
        // Windows 10 2004+: leave the mouse pointer out (it would tint the zones it crosses).
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                session.IsCursorCaptureEnabled = false;
        }
        catch (Exception e)
        {
            Log.Write($"capture: cursor: {e.Message}");
        }
        // Windows 11: no yellow border around what's captured.
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                if (!_askedBorderless)
                {
                    _askedBorderless = true;
                    // Unpackaged apps are allowed without a prompt; asking once doesn't hurt.
                    _ = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                }
                session.IsBorderRequired = false;
            }
        }
        catch (Exception e)
        {
            Log.Write($"capture: border: {e.Message}");
        }
    }

    void OnItemClosed(GraphicsCaptureItem sender, object args) => _broken = true;

    void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            lock (_gate)
            {
                if (_disposed) return;
                var now = _clock.ElapsedTicks;
                // Frames come at the display's refresh rate while the content changes: keep `fps`.
                if (_last != long.MinValue && now - _last < _interval * 9 / 10) return;
                _last = now;

                var content = frame.ContentSize;
                if (content.Width <= 0 || content.Height <= 0) return;
                if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                {
                    // The window was resized: following frames come at the new size.
                    _poolSize = content;
                    _pool.Recreate(_device.WinRT, Format, 2, content);
                }
                using var surface = frame.Surface;
                var texture = WinRTInterop.Texture(surface);
                try
                {
                    Reduce(texture, (uint)content.Width, (uint)content.Height);
                }
                finally
                {
                    D3D11.Release(ref texture);
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"capture: frame: {e.Message}");
            _broken = true;
        }
    }

    void Reduce(IntPtr texture, uint contentWidth, uint contentHeight)
    {
        var desc = D3D11.GetDesc(texture);
        var width = Math.Min(contentWidth, desc.Width);
        var height = Math.Min(contentHeight, desc.Height);
        if (width == 0 || height == 0) return;
        if (width != _mipWidth || height != _mipHeight || _mipTexture == IntPtr.Zero) Allocate(width, height);

        var context = _device.Context;
        var box = new D3D11_BOX { Left = 0, Top = 0, Front = 0, Right = width, Bottom = height, Back = 1 };
        D3D11.CopySubresourceRegion(context, _mipTexture, 0, texture, 0, box);
        D3D11.GenerateMips(context, _mipView);
        D3D11.CopySubresourceRegion(context, _staging, 0, _mipTexture, _level, null);
        var hr = D3D11.Map(context, _staging, 0, D3D11.D3D11_MAP_READ, out var mapped);
        if (hr < 0)
        {
            Log.Write($"capture: map 0x{hr:X8}");
            _broken = true;
            return;
        }
        try
        {
            unsafe
            {
                var pixels = new ReadOnlySpan<byte>((void*)mapped.pData, checked((int)(mapped.RowPitch * _levelHeight)));
                _sink(pixels, (int)_levelWidth, (int)_levelHeight, (int)mapped.RowPitch);
            }
        }
        finally
        {
            D3D11.Unmap(context, _staging, 0);
        }
    }

    /// <summary>Mipmapped copy of the content size and a staging texture for the level nearest 256 px wide.</summary>
    void Allocate(uint width, uint height)
    {
        FreeTextures();
        _mipWidth = width;
        _mipHeight = height;
        var levels = 1u;
        while ((Math.Max(width, height) >> (int)levels) > 0) levels++;
        // Level whose width is closest to the target (on a log scale), but at least 16 px tall.
        _level = 0;
        var best = double.MaxValue;
        for (var l = 0u; l < levels; l++)
        {
            var w = Math.Max(1u, width >> (int)l);
            var h = Math.Max(1u, height >> (int)l);
            if (l > 0 && h < 16) break;
            var score = Math.Abs(Math.Log((double)w / TargetWidth));
            if (score < best)
            {
                best = score;
                _level = l;
            }
        }
        _levelWidth = Math.Max(1u, width >> (int)_level);
        _levelHeight = Math.Max(1u, height >> (int)_level);

        var mipDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = width, Height = height, MipLevels = _level + 1, ArraySize = 1,
            Format = D3D11.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, Usage = D3D11.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11.D3D11_BIND_SHADER_RESOURCE | D3D11.D3D11_BIND_RENDER_TARGET,
            MiscFlags = D3D11.D3D11_RESOURCE_MISC_GENERATE_MIPS,
        };
        _mipTexture = D3D11.CreateTexture2D(_device.Device, mipDesc);
        _mipView = D3D11.CreateShaderResourceView(_device.Device, _mipTexture);
        var stagingDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = _levelWidth, Height = _levelHeight, MipLevels = 1, ArraySize = 1,
            Format = D3D11.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, Usage = D3D11.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11.D3D11_CPU_ACCESS_READ,
        };
        _staging = D3D11.CreateTexture2D(_device.Device, stagingDesc);
    }

    void FreeTextures()
    {
        D3D11.Release(ref _staging);
        D3D11.Release(ref _mipView);
        D3D11.Release(ref _mipTexture);
        _mipWidth = _mipHeight = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            FreeTextures();
        }
        // Outside the lock: closing may wait for a frame handler that is waiting for the lock.
        _pool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnItemClosed;
        try { _session.Dispose(); } catch { /* already gone */ }
        try { _pool.Dispose(); } catch { /* already gone */ }
    }
}
