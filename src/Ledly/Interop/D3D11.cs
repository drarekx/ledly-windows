using System.Runtime.InteropServices;

namespace Ledly.Windows.Interop;

[StructLayout(LayoutKind.Sequential)]
struct D3D11_TEXTURE2D_DESC
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public uint Format;
    public uint SampleCount;
    public uint SampleQuality;
    public uint Usage;
    public uint BindFlags;
    public uint CPUAccessFlags;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
struct D3D11_BOX
{
    public uint Left, Top, Front, Right, Bottom, Back;
}

[StructLayout(LayoutKind.Sequential)]
struct D3D11_MAPPED_SUBRESOURCE
{
    public IntPtr pData;
    public uint RowPitch;
    public uint DepthPitch;
}

/// <summary>
/// The few Direct3D 11 calls the capture needs, made through the COM vtables (no wrapper library).
/// Slot numbers follow d3d11.h: IUnknown 0–2, ID3D11DeviceChild 3–6, then each interface's own methods.
/// </summary>
static unsafe class D3D11
{
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const uint D3D11_USAGE_DEFAULT = 0, D3D11_USAGE_STAGING = 3;
    public const uint D3D11_BIND_SHADER_RESOURCE = 0x8, D3D11_BIND_RENDER_TARGET = 0x20;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_RESOURCE_MISC_GENERATE_MIPS = 0x1;
    public const uint D3D11_MAP_READ = 1;
    const uint D3D_DRIVER_TYPE_HARDWARE = 1, D3D_DRIVER_TYPE_WARP = 5;
    const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    const uint D3D11_SDK_VERSION = 7;

    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);

    /// <summary>Wraps a DXGI device as a WinRT IDirect3DDevice (returns an IInspectable*).</summary>
    [DllImport("d3d11.dll")]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>Hardware device with BGRA support, or WARP if there is no usable GPU.</summary>
    public static (IntPtr Device, IntPtr Context) CreateDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out var device, out _, out var context);
        if (hr < 0)
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out _, out context);
        Marshal.ThrowExceptionForHR(hr);
        return (device, context);
    }

    static void** Vtbl(IntPtr obj) => *(void***)obj;

    public static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        Marshal.Release(obj);
        obj = IntPtr.Zero;
    }

    // ID3D11Device ------------------------------------------------------------------------------

    /// <summary>ID3D11Device::CreateTexture2D (slot 5).</summary>
    public static IntPtr CreateTexture2D(IntPtr device, in D3D11_TEXTURE2D_DESC desc)
    {
        IntPtr texture;
        int hr;
        fixed (D3D11_TEXTURE2D_DESC* d = &desc)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Vtbl(device)[5])(device, d, IntPtr.Zero, &texture);
        Marshal.ThrowExceptionForHR(hr);
        return texture;
    }

    /// <summary>ID3D11Device::CreateShaderResourceView (slot 7), default view of the whole resource.</summary>
    public static IntPtr CreateShaderResourceView(IntPtr device, IntPtr resource)
    {
        IntPtr view;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Vtbl(device)[7])(device, resource, IntPtr.Zero, &view);
        Marshal.ThrowExceptionForHR(hr);
        return view;
    }

    /// <summary>ID3D11Device::GetDeviceRemovedReason (slot 39): S_OK while the device is fine.</summary>
    public static int GetDeviceRemovedReason(IntPtr device) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(device)[39])(device);

    // ID3D11Texture2D ---------------------------------------------------------------------------

    /// <summary>ID3D11Texture2D::GetDesc (slot 10, after ID3D11Resource's 7–9).</summary>
    public static D3D11_TEXTURE2D_DESC GetDesc(IntPtr texture)
    {
        D3D11_TEXTURE2D_DESC desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Vtbl(texture)[10])(texture, &desc);
        return desc;
    }

    // ID3D11DeviceContext -----------------------------------------------------------------------

    /// <summary>ID3D11DeviceContext::Map (slot 14).</summary>
    public static int Map(IntPtr context, IntPtr resource, uint subresource, uint mapType, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        D3D11_MAPPED_SUBRESOURCE m;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Vtbl(context)[14])(
            context, resource, subresource, mapType, 0, &m);
        mapped = m;
        return hr;
    }

    /// <summary>ID3D11DeviceContext::Unmap (slot 15).</summary>
    public static void Unmap(IntPtr context, IntPtr resource, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtbl(context)[15])(context, resource, subresource);

    /// <summary>ID3D11DeviceContext::CopySubresourceRegion (slot 46). Null box = whole subresource.</summary>
    public static void CopySubresourceRegion(IntPtr context, IntPtr dst, uint dstSubresource, IntPtr src, uint srcSubresource, D3D11_BOX? box)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)Vtbl(context)[46];
        if (box is { } b)
            fn(context, dst, dstSubresource, 0, 0, 0, src, srcSubresource, &b);
        else
            fn(context, dst, dstSubresource, 0, 0, 0, src, srcSubresource, null);
    }

    /// <summary>ID3D11DeviceContext::GenerateMips (slot 54).</summary>
    public static void GenerateMips(IntPtr context, IntPtr shaderResourceView) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Vtbl(context)[54])(context, shaderResourceView);

    // IDirect3DDxgiInterfaceAccess --------------------------------------------------------------

    /// <summary>IDirect3DDxgiInterfaceAccess::GetInterface (slot 3): the texture behind a WinRT surface.</summary>
    public static IntPtr GetInterface(IntPtr access, Guid iid)
    {
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(access)[3])(access, &iid, &result);
        Marshal.ThrowExceptionForHR(hr);
        return result;
    }
}
