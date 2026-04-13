using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

public enum GpuBackendKind
{
    CpuOnly,
    Cuda,
    Vulkan,
    Metal,
    CoreML,
}

public sealed record GpuCapability(GpuBackendKind Kind, string DeviceName, long VramBytes, string Summary);

/// <summary>
/// Best-effort detection of available GPU acceleration for Whisper.net and LLamaSharp.
/// Probes native libraries via <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/> without taking
/// a hard dependency on CUDA / Vulkan / Metal SDKs. Detection is cheap (sub-100 ms), not persisted,
/// and never throws — failures degrade to <see cref="GpuBackendKind.CpuOnly"/>.
/// </summary>
public static class GpuCapabilityDetector
{
    private static readonly ILogger Log = AppLog.CreateLogger(nameof(GpuCapabilityDetector));

    private const int NvidiaSmiTimeoutMs = 500;

    public static GpuCapability Detect()
    {
        try
        {
            // macOS → Metal / CoreML (always available on modern Macs; Whisper.net has a CoreML runtime).
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var deviceName = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Apple Silicon" : "Apple Mac";
                var summary = $"Metal / CoreML ({deviceName})";
                Log.LogDebug("GPU detection: macOS → Metal ({Device})", deviceName);
                return new GpuCapability(GpuBackendKind.Metal, deviceName, 0, summary);
            }

            // Windows / Linux → try CUDA first, then Vulkan.
            if (TryProbeCuda(out var cuda))
            {
                Log.LogDebug("GPU detection: CUDA available ({Device}, {VramBytes} bytes)", cuda.DeviceName, cuda.VramBytes);
                return cuda;
            }

            if (TryProbeVulkan(out var vulkan))
            {
                Log.LogDebug("GPU detection: Vulkan available");
                return vulkan;
            }

            Log.LogDebug("GPU detection: no accelerator found, CPU only");
            return new GpuCapability(GpuBackendKind.CpuOnly, "", 0, "CPU only — Whisper and LLM will run on CPU (slower)");
        }
        catch (Exception ex)
        {
            // Detector MUST NOT throw — degrade silently to CPU on any unexpected failure.
            Log.LogWarning(ex, "GPU detection failed unexpectedly; falling back to CPU");
            return new GpuCapability(GpuBackendKind.CpuOnly, "", 0, "CPU only (detection error)");
        }
    }

    private static bool TryProbeCuda(out GpuCapability capability)
    {
        capability = new GpuCapability(GpuBackendKind.CpuOnly, "", 0, "");

        var driverLib = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "nvcuda.dll" : "libcuda.so.1";

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(driverLib, out handle))
            {
                return false;
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
            }
        }

        // Driver library is present — NVIDIA GPU exists. Try nvidia-smi for friendly name + VRAM.
        var (deviceName, vramBytes) = TryQueryNvidiaSmi();
        if (string.IsNullOrEmpty(deviceName))
        {
            deviceName = "NVIDIA GPU";
        }

        var vramStr = vramBytes > 0 ? $" ({vramBytes / (1024 * 1024 * 1024)} GB)" : "";
        capability = new GpuCapability(GpuBackendKind.Cuda, deviceName, vramBytes, $"CUDA: {deviceName}{vramStr}");
        return true;
    }

    private static bool TryProbeVulkan(out GpuCapability capability)
    {
        capability = new GpuCapability(GpuBackendKind.CpuOnly, "", 0, "");

        var vulkanLib = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vulkan-1.dll" : "libvulkan.so.1";

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(vulkanLib, out handle))
            {
                return false;
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
            }
        }

        capability = new GpuCapability(GpuBackendKind.Vulkan, "", 0, "Vulkan (device name not probed)");
        return true;
    }

    private static (string DeviceName, long VramBytes) TryQueryNvidiaSmi()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!proc.Start())
            {
                return ("", 0);
            }

            if (!proc.WaitForExit(NvidiaSmiTimeoutMs))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited — nothing to do.
                }

                return ("", 0);
            }

            if (proc.ExitCode != 0)
            {
                return ("", 0);
            }

            var output = proc.StandardOutput.ReadToEnd();
            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return ("", 0);
            }

            var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                return ("", 0);
            }

            var name = parts[0];
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vramMib))
            {
                return (name, 0);
            }

            return (name, vramMib * 1024L * 1024L);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "nvidia-smi query failed");
            return ("", 0);
        }
    }
}
