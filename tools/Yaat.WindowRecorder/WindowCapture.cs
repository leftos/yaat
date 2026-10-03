using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Yaat.WindowRecorder;

/// <summary>
/// A Windows.Graphics.Capture session on one window: the frame pool at the window's size, a staging texture the newest
/// frame is copied into, and a read-back of that texture as top-down BGRA rows packed at <see cref="OutputSize"/>.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private const string SessionTypeName = "Windows.Graphics.Capture.GraphicsCaptureSession";
    private const int BytesPerPixel = 4;

    private readonly GraphicsCaptureItem _item;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly ID3D11Texture2D _staging;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    public WindowCapture(nint hwnd)
    {
        _item = CaptureInterop.CreateItemForWindow(hwnd);
        Size = _item.Size;
        OutputSize = new SizeInt32 { Width = RoundUpToEven(Size.Width), Height = RoundUpToEven(Size.Height) };
        _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _context = _device.ImmediateContext;
        _winRtDevice = CaptureInterop.CreateWinRtDevice(_device);
        _staging = _device.CreateTexture2D(
            new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)Size.Width,
                (uint)Size.Height,
                arraySize: 1,
                mipLevels: 1,
                bindFlags: BindFlags.None,
                usage: ResourceUsage.Staging,
                cpuAccessFlags: CpuAccessFlags.Read
            )
        );
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, Size);
        _session = _framePool.CreateCaptureSession(_item);
        ConfigureSession(_session);
    }

    /// <summary>The window's client size when the capture was created; every frame is this size.</summary>
    public SizeInt32 Size { get; }

    /// <summary>
    /// <see cref="Size"/> with an odd dimension rounded up to even: yuv420p stores one chroma sample per 2x2 block, so an
    /// encoder refuses an odd size. The extra column or row stays black, where a crop would drop one of the window's.
    /// </summary>
    public SizeInt32 OutputSize { get; }

    public int FrameBytes => OutputSize.Width * OutputSize.Height * BytesPerPixel;

    public void Start() => _session.StartCapture();

    /// <summary>
    /// Drains the pool, copies the newest frame into <paramref name="buffer"/> and returns true; returns false when no
    /// frame arrived since the last call, so the caller writes the previous one again. Throws
    /// <see cref="WindowResizedException"/> when the window's content size no longer matches the pool.
    /// </summary>
    public bool TryCopyLatestFrame(byte[] buffer)
    {
        Direct3D11CaptureFrame? newest = null;
        while (_framePool.TryGetNextFrame() is { } frame)
        {
            newest?.Dispose();
            newest = frame;
        }
        if (newest is null)
        {
            return false;
        }
        using (newest)
        {
            SizeInt32 contentSize = newest.ContentSize;
            if (contentSize.Width != Size.Width || contentSize.Height != Size.Height)
            {
                throw new WindowResizedException(Size, contentSize);
            }
            using ID3D11Texture2D texture = CaptureInterop.GetTexture(newest.Surface);
            _context.CopyResource(_staging, texture);
            MappedSubresource mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyRows(mapped, buffer);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }
        return true;
    }

    public void Dispose()
    {
        _session.Dispose();
        _framePool.Dispose();
        _staging.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private static void ConfigureSession(GraphicsCaptureSession session)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) && ApiInformation.IsPropertyPresent(SessionTypeName, "IsBorderRequired"))
        {
            session.IsBorderRequired = false;
        }
        if (ApiInformation.IsPropertyPresent(SessionTypeName, "IsCursorCaptureEnabled"))
        {
            session.IsCursorCaptureEnabled = false;
        }
    }

    private static int RoundUpToEven(int value) => value + (value & 1);

    /// <summary>
    /// The mapped texture pads each row to its pitch; the output wants rows at the output width, edge to edge. Only the
    /// window's own pixels are written, so an odd size's pad column and row keep the zeros the buffer was allocated with.
    /// </summary>
    private void CopyRows(MappedSubresource mapped, byte[] buffer)
    {
        int contentBytes = Size.Width * BytesPerPixel;
        int outputBytes = OutputSize.Width * BytesPerPixel;
        int pitch = (int)mapped.RowPitch;
        for (int row = 0; row < Size.Height; row++)
        {
            Marshal.Copy(mapped.DataPointer + row * pitch, buffer, row * outputBytes, contentBytes);
        }
    }
}
