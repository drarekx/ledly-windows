using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Ledly.Windows.Interop;

/// <summary>Bridges between Win32/D3D11 handles and the Windows.Graphics.Capture projections.</summary>
static unsafe class WinRTInterop
{
    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_ID3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    static IntPtr CaptureItemInterop()
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, IID_IGraphicsCaptureItemInterop, out var factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>IGraphicsCaptureItemInterop::CreateForWindow (slot 3) / CreateForMonitor (slot 4).</summary>
    static GraphicsCaptureItem Create(IntPtr handle, int slot)
    {
        var factory = CaptureItemInterop();
        try
        {
            var iid = IID_IGraphicsCaptureItem;
            IntPtr item;
            var vtbl = *(void***)factory;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[slot])(factory, handle, &iid, &item);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd) => Create(hwnd, 3);
    public static GraphicsCaptureItem CreateForMonitor(IntPtr monitor) => Create(monitor, 4);

    /// <summary>WinRT IDirect3DDevice over a D3D11 device. The capture also uses the device from its own threads.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(IntPtr d3dDevice)
    {
        // ID3D10Multithread::SetMultithreadProtected(TRUE) (slot 5): the frame pool touches the device too.
        if (Marshal.QueryInterface(d3dDevice, in IID_ID3D10Multithread, out var multithread) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)(*(void***)multithread)[5])(multithread, 1);
            Marshal.Release(multithread);
        }
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in D3D11.IID_IDXGIDevice, out var dxgi));
        try
        {
            Marshal.ThrowExceptionForHR(D3D11.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>The ID3D11Texture2D behind a captured frame's surface (caller releases it).</summary>
    public static IntPtr Texture(IDirect3DSurface surface)
    {
        var unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in D3D11.IID_IDirect3DDxgiInterfaceAccess, out var access));
            try
            {
                return D3D11.GetInterface(access, D3D11.IID_ID3D11Texture2D);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
