using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services.Speech;

/// <summary>
/// The CUDA driver API calls the GPU check makes. Each call returns the driver's <c>CUresult</c> (0 is success), or null when
/// the driver library lacks that export.
/// </summary>
public interface ICudaDriverApi
{
    /// <summary>Loads the driver library; false when it is not there, which means no NVIDIA driver.</summary>
    bool TryLoad();

    /// <summary><c>cuInit</c>.</summary>
    /// <param name="flags">The init flags; must be 0.</param>
    int? Init(uint flags);

    /// <summary><c>cuDriverGetVersion</c>: the newest CUDA the driver supports, as <c>1000 * major + 10 * minor</c>.</summary>
    /// <param name="driverVersion">The version.</param>
    int? DriverGetVersion(out int driverVersion);

    /// <summary><c>cuDeviceGet</c>.</summary>
    /// <param name="device">The device handle.</param>
    /// <param name="ordinal">The device's ordinal.</param>
    int? DeviceGet(out int device, int ordinal);

    /// <summary><c>cuDeviceGetName</c>.</summary>
    /// <param name="name">The device's name.</param>
    /// <param name="device">The device handle.</param>
    int? DeviceGetName(out string name, int device);

    /// <summary><c>cuDeviceGetAttribute</c>.</summary>
    /// <param name="value">The attribute's value.</param>
    /// <param name="attribute">The <c>CUdevice_attribute</c>.</param>
    /// <param name="device">The device handle.</param>
    int? DeviceGetAttribute(out int value, int attribute, int device);
}

/// <summary>What the GPU check found on CUDA device 0, and whether GPU speech can run there.</summary>
/// <param name="DeviceName">The card's name; null when the check stopped before reading it.</param>
/// <param name="ComputeMajor">The card's compute capability major number, or null.</param>
/// <param name="ComputeMinor">The card's compute capability minor number, or null.</param>
/// <param name="DriverCudaVersion">The newest CUDA the driver supports (<c>1000 * major + 10 * minor</c>), or null.</param>
/// <param name="IsSupported">Whether the CUDA 13 runtime can run on this card and driver.</param>
/// <param name="Reason">The sentence the Device row shows.</param>
public sealed record GpuSupport(string? DeviceName, int? ComputeMajor, int? ComputeMinor, int? DriverCudaVersion, bool IsSupported, string Reason);

/// <summary>
/// Checks whether GPU speech can run: an NVIDIA driver new enough for CUDA 13 (R580, <c>cuDriverGetVersion</c> at least 13000)
/// and a CUDA device 0, the one ONNX Runtime's CUDA provider uses by default, of compute capability 7.5 or newer. Uses the
/// CUDA driver API only, which ships with the driver, so it needs no toolkit or runtime download.
/// </summary>
public static class CudaDriverProbe
{
    /// <summary>The Device row's text while the check runs.</summary>
    public const string CheckingText = "Checking your GPU…";

    private const int MinDriverCudaVersion = 13000;
    private const int MinComputeMajor = 7;
    private const int MinComputeMinor = 5;
    private const int DeviceOrdinal = 0;
    private const int ComputeCapabilityMajorAttribute = 75;
    private const int ComputeCapabilityMinorAttribute = 76;

    private const string UnsupportedPlatformReason = "GPU speech is available on Windows and Linux x64 only.";
    private const string NoDriverReason = "No NVIDIA GPU driver found; GPU speech needs an NVIDIA card with driver 580 or newer.";

    private static readonly ILogger Log = AppLog.CreateLogger("CudaDriverProbe");

    /// <summary>
    /// Runs the check on the thread pool against the process's NVIDIA driver. Never faults: a throw from the driver calls is
    /// logged and reads as no driver.
    /// </summary>
    public static Task<GpuSupport> ProbeAsync() =>
        Task.Run(() =>
        {
            try
            {
                return Probe(new CudaDriverNativeApi(), IsSupportedPlatform());
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "The GPU check failed; treating it as no NVIDIA driver");
                return new GpuSupport(null, null, null, null, false, NoDriverReason);
            }
        });

    /// <summary>Runs the check against <paramref name="api"/>; an unsupported platform never touches it.</summary>
    /// <param name="api">The driver API.</param>
    /// <param name="isSupportedPlatform">Whether this is Windows x64 or Linux x64, the platforms with a GPU runtime.</param>
    public static GpuSupport Probe(ICudaDriverApi api, bool isSupportedPlatform)
    {
        if (!isSupportedPlatform)
        {
            return Unsupported(UnsupportedPlatformReason);
        }
        if (!api.TryLoad() || !Succeeded(api.Init(0), "cuInit") || !Succeeded(api.DriverGetVersion(out int driverVersion), "cuDriverGetVersion"))
        {
            return Unsupported(NoDriverReason);
        }
        if (driverVersion < MinDriverCudaVersion)
        {
            string cuda = $"{driverVersion / 1000}.{(driverVersion % 1000) / 10}";
            return new GpuSupport(
                null,
                null,
                null,
                driverVersion,
                false,
                $"Your NVIDIA driver supports CUDA {cuda}; GPU speech needs driver 580 or newer (CUDA 13)."
            );
        }
        return ProbeDevice(api, driverVersion);
    }

    private static GpuSupport ProbeDevice(ICudaDriverApi api, int driverVersion)
    {
        if (
            !Succeeded(api.DeviceGet(out int device, DeviceOrdinal), "cuDeviceGet")
            || !Succeeded(api.DeviceGetName(out string name, device), "cuDeviceGetName")
            || !Succeeded(api.DeviceGetAttribute(out int major, ComputeCapabilityMajorAttribute, device), "cuDeviceGetAttribute")
            || !Succeeded(api.DeviceGetAttribute(out int minor, ComputeCapabilityMinorAttribute, device), "cuDeviceGetAttribute")
        )
        {
            return Unsupported(NoDriverReason);
        }

        bool supported = (major > MinComputeMajor) || ((major == MinComputeMajor) && (minor >= MinComputeMinor));
        string reason = supported
            ? $"{name} (compute {major}.{minor}) supports GPU speech."
            : $"{name} (compute {major}.{minor}) is too old for CUDA 13, which needs compute 7.5 or newer.";
        Log.LogInformation("GPU check: {Name}, compute {Major}.{Minor}, driver CUDA {Driver}: {Reason}", name, major, minor, driverVersion, reason);
        return new GpuSupport(name, major, minor, driverVersion, supported, reason);
    }

    private static GpuSupport Unsupported(string reason)
    {
        Log.LogInformation("GPU check: {Reason}", reason);
        return new GpuSupport(null, null, null, null, false, reason);
    }

    private static bool Succeeded(int? result, string call)
    {
        if (result == 0)
        {
            return true;
        }
        Log.LogInformation("GPU check: {Call} {Outcome}", call, (result is int code) ? $"returned CUresult {code}" : "is not exported by the driver");
        return false;
    }

    private static bool IsSupportedPlatform() =>
        (RuntimeInformation.ProcessArchitecture == Architecture.X64) && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());
}

/// <summary>
/// The CUDA driver API of the installed NVIDIA driver: <c>nvcuda.dll</c> on Windows, <c>libcuda.so.1</c> on Linux, loaded by
/// name with <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/> and called through its exports. The library stays loaded
/// for the life of the process.
/// </summary>
public sealed class CudaDriverNativeApi : ICudaDriverApi
{
    private const int NameBufferLength = 256;

    private IntPtr _library;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int InitFn(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DriverGetVersionFn(out int driverVersion);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DeviceGetFn(out int device, int ordinal);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DeviceGetNameFn([Out] byte[] name, int length, int device);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DeviceGetAttributeFn(out int value, int attribute, int device);

    /// <inheritdoc />
    public bool TryLoad() => NativeLibrary.TryLoad(OperatingSystem.IsWindows() ? "nvcuda.dll" : "libcuda.so.1", out _library);

    /// <inheritdoc />
    public int? Init(uint flags) => Export<InitFn>("cuInit")?.Invoke(flags);

    /// <inheritdoc />
    public int? DriverGetVersion(out int driverVersion)
    {
        driverVersion = 0;
        return Export<DriverGetVersionFn>("cuDriverGetVersion")?.Invoke(out driverVersion);
    }

    /// <inheritdoc />
    public int? DeviceGet(out int device, int ordinal)
    {
        device = 0;
        return Export<DeviceGetFn>("cuDeviceGet")?.Invoke(out device, ordinal);
    }

    /// <inheritdoc />
    public int? DeviceGetName(out string name, int device)
    {
        name = "";
        DeviceGetNameFn? call = Export<DeviceGetNameFn>("cuDeviceGetName");
        if (call is null)
        {
            return null;
        }
        byte[] buffer = new byte[NameBufferLength];
        int result = call(buffer, buffer.Length, device);
        int end = Array.IndexOf(buffer, (byte)0);
        name = Encoding.UTF8.GetString(buffer, 0, (end < 0) ? buffer.Length : end);
        return result;
    }

    /// <inheritdoc />
    public int? DeviceGetAttribute(out int value, int attribute, int device)
    {
        value = 0;
        return Export<DeviceGetAttributeFn>("cuDeviceGetAttribute")?.Invoke(out value, attribute, device);
    }

    private T? Export<T>(string name)
        where T : Delegate =>
        ((_library != IntPtr.Zero) && NativeLibrary.TryGetExport(_library, name, out IntPtr address))
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;
}
