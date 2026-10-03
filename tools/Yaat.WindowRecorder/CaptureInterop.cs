using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Yaat.WindowRecorder;

/// <summary>
/// The three COM seams the WinRT projection does not cover: a capture item from an HWND, a WinRT device from a DXGI
/// device, and the D3D11 texture behind a captured surface.
/// </summary>
internal static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);

        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(in Guid iid);
    }

    /// <summary>Creates the capture item for a window through the activation factory's desktop interop interface.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        IGraphicsCaptureItemInterop interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        nint itemPointer = interop.CreateForWindow(hwnd, GraphicsCaptureItemIid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    /// <summary>Wraps a D3D11 device as the WinRT device the frame pool takes.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint devicePointer));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(devicePointer);
        }
        finally
        {
            Marshal.Release(devicePointer);
        }
    }

    /// <summary>Returns the D3D11 texture a captured frame's surface is backed by; the caller disposes it.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IDirect3DDxgiInterfaceAccess access = surface.As<IDirect3DDxgiInterfaceAccess>();
        return new ID3D11Texture2D(access.GetInterface(Texture2DIid));
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
