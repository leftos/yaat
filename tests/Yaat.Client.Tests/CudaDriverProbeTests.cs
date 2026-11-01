using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>The GPU check against a fake CUDA driver API, one test per row of the probe's case table.</summary>
public sealed class CudaDriverProbeTests
{
    private const string NoDriver = "No NVIDIA GPU driver found; GPU speech needs an NVIDIA card with driver 580 or newer.";

    [Fact]
    public void Unsupported_Platform_Never_Touches_The_Api()
    {
        var api = new FakeCudaDriverApi();

        GpuSupport support = CudaDriverProbe.Probe(api, isSupportedPlatform: false);

        Assert.False(support.IsSupported);
        Assert.Equal("GPU speech is available on Windows and Linux x64 only.", support.Reason);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Missing_Library_Means_No_Driver()
    {
        var api = new FakeCudaDriverApi { Loads = false };

        GpuSupport support = CudaDriverProbe.Probe(api, isSupportedPlatform: true);

        Assert.Equal(new GpuSupport(null, null, null, null, false, NoDriver), support);
        Assert.Equal(["TryLoad"], api.Calls);
    }

    [Fact]
    public void CuInit_Error_Means_No_Driver()
    {
        var api = new FakeCudaDriverApi { InitResult = 100 };

        GpuSupport support = CudaDriverProbe.Probe(api, isSupportedPlatform: true);

        Assert.Equal(new GpuSupport(null, null, null, null, false, NoDriver), support);
        Assert.Equal(["TryLoad", "cuInit"], api.Calls);
    }

    [Fact]
    public void Driver_Below_Cuda_13_Is_Too_Old()
    {
        var api = new FakeCudaDriverApi { DriverVersion = 12090 };

        GpuSupport support = CudaDriverProbe.Probe(api, isSupportedPlatform: true);

        Assert.False(support.IsSupported);
        Assert.Equal(12090, support.DriverCudaVersion);
        Assert.Equal("Your NVIDIA driver supports CUDA 12.9; GPU speech needs driver 580 or newer (CUDA 13).", support.Reason);
    }

    [Fact]
    public void Driver_13000_With_Compute_7_5_Is_Supported_At_The_Boundary()
    {
        var api = new FakeCudaDriverApi
        {
            DriverVersion = 13000,
            ComputeMajor = 7,
            ComputeMinor = 5,
            Name = "NVIDIA GeForce RTX 2060",
        };

        GpuSupport support = CudaDriverProbe.Probe(api, isSupportedPlatform: true);

        Assert.Equal(
            new GpuSupport("NVIDIA GeForce RTX 2060", 7, 5, 13000, true, "NVIDIA GeForce RTX 2060 (compute 7.5) supports GPU speech."),
            support
        );
        Assert.Equal(["TryLoad", "cuInit", "cuDriverGetVersion", "cuDeviceGet 0", "cuDeviceGetName", "attribute 75", "attribute 76"], api.Calls);
    }

    [Fact]
    public void Compute_7_0_Is_Too_Low()
    {
        var api = new FakeCudaDriverApi
        {
            DriverVersion = 13020,
            ComputeMajor = 7,
            ComputeMinor = 0,
            Name = "Tesla V100",
        };

        GpuSupport support = CudaDriverProbe.Probe(api, isSupportedPlatform: true);

        Assert.False(support.IsSupported);
        Assert.Equal("Tesla V100 (compute 7.0) is too old for CUDA 13, which needs compute 7.5 or newer.", support.Reason);
    }

    [Fact]
    public void Compute_8_6_Is_Supported()
    {
        var api = new FakeCudaDriverApi
        {
            DriverVersion = 13040,
            ComputeMajor = 8,
            ComputeMinor = 6,
            Name = "NVIDIA GeForce RTX 3080",
        };

        GpuSupport support = CudaDriverProbe.Probe(api, isSupportedPlatform: true);

        Assert.True(support.IsSupported);
        Assert.Equal("NVIDIA GeForce RTX 3080 (compute 8.6) supports GPU speech.", support.Reason);
    }

    [Fact]
    public void An_Export_Missing_Mid_Way_Means_No_Driver()
    {
        var api = new FakeCudaDriverApi { DriverVersion = 13000, MissingExport = "cuDeviceGetName" };

        GpuSupport support = CudaDriverProbe.Probe(api, isSupportedPlatform: true);

        Assert.False(support.IsSupported);
        Assert.Equal(NoDriver, support.Reason);
        Assert.Equal(["TryLoad", "cuInit", "cuDriverGetVersion", "cuDeviceGet 0", "cuDeviceGetName"], api.Calls);
    }

    [Fact]
    public void The_Pending_Text_Is_Checking_Your_Gpu() => Assert.Equal("Checking your GPU…", CudaDriverProbe.CheckingText);

    /// <summary>A driver whose answers the test sets; a call named by <see cref="MissingExport"/> reports its export missing.</summary>
    private sealed class FakeCudaDriverApi : ICudaDriverApi
    {
        public bool Loads { get; init; } = true;

        public int InitResult { get; init; }

        public int DriverVersion { get; init; } = 13000;

        public int ComputeMajor { get; init; } = 8;

        public int ComputeMinor { get; init; }

        public string Name { get; init; } = "GPU";

        public string? MissingExport { get; init; }

        public List<string> Calls { get; } = [];

        public bool TryLoad()
        {
            Calls.Add("TryLoad");
            return Loads;
        }

        public int? Init(uint flags)
        {
            Calls.Add("cuInit");
            return Answer("cuInit", InitResult);
        }

        public int? DriverGetVersion(out int driverVersion)
        {
            Calls.Add("cuDriverGetVersion");
            driverVersion = DriverVersion;
            return Answer("cuDriverGetVersion", 0);
        }

        public int? DeviceGet(out int device, int ordinal)
        {
            Calls.Add($"cuDeviceGet {ordinal}");
            device = ordinal;
            return Answer("cuDeviceGet", 0);
        }

        public int? DeviceGetName(out string name, int device)
        {
            Calls.Add("cuDeviceGetName");
            name = Name;
            return Answer("cuDeviceGetName", 0);
        }

        public int? DeviceGetAttribute(out int value, int attribute, int device)
        {
            Calls.Add($"attribute {attribute}");
            value = attribute switch
            {
                75 => ComputeMajor,
                76 => ComputeMinor,
                _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, "The probe asks for compute capability only."),
            };
            return Answer("cuDeviceGetAttribute", 0);
        }

        private int? Answer(string export, int result) => (export == MissingExport) ? null : result;
    }
}
