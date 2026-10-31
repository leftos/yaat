using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The GPU-use check of a <c>--corpus-wer</c> run: which "GPU Process Memory" instance belongs to a process,
/// and what the GPU memory figures around a model load say about the device the run asked for.
/// </summary>
public sealed class GpuMemoryProbeTests
{
    private const long Megabyte = 1024 * 1024;

    [Theory]
    [InlineData("pid_123_luid_0x00000000_0x0000C2D1_phys_0", 123, true)]
    [InlineData("pid_1234_luid_0x00000000_0x0000C2D1_phys_0", 123, false)]
    [InlineData("pid_12_luid_0x00000000_0x0000C2D1_phys_0", 123, false)]
    [InlineData("", 123, false)]
    public void Only_An_Instance_Named_For_The_Process_Matches(string instanceName, int pid, bool expected) =>
        Assert.Equal(expected, GpuMemoryProbe.MatchesProcess(instanceName, pid));

    [Fact]
    public void A_GPU_Run_Whose_Memory_Rose_Less_Than_The_Minimum_Is_An_Error()
    {
        string? verdict = GpuUseCheck.Verdict("gpu", 0, 99 * Megabyte, out bool isError);

        Assert.True(isError);
        Assert.NotNull(verdict);
        Assert.Contains("GPU mode asked", verdict, StringComparison.Ordinal);
        Assert.Contains("rose only 99 MB", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void A_GPU_Run_Whose_Memory_Rose_By_The_Minimum_Is_Not_Reported() =>
        Assert.Null(GpuUseCheck.Verdict("gpu", 512 * Megabyte, 612 * Megabyte, out bool _));

    [Theory]
    [InlineData(-1, 512)]
    [InlineData(512, -1)]
    public void A_GPU_Run_Whose_Counter_Could_Not_Be_Read_Is_An_Error(long preLoadMb, long afterLoadMb)
    {
        string? verdict = GpuUseCheck.Verdict("gpu", preLoadMb * Megabyte, afterLoadMb * Megabyte, out bool isError);

        Assert.True(isError);
        Assert.Equal("GPU mode asked, but the GPU memory counter could not be read, so GPU use cannot be verified", verdict);
    }

    [Fact]
    public void A_CPU_Run_Whose_Memory_Rose_By_The_Minimum_Is_A_Warning()
    {
        string? verdict = GpuUseCheck.Verdict("cpu", 0, 100 * Megabyte, out bool isError);

        Assert.False(isError);
        Assert.Equal("CPU mode, but the process's GPU memory rose 100 MB through model load", verdict);
    }

    [Fact]
    public void A_CPU_Run_Whose_Memory_Rose_Less_Than_The_Minimum_Is_Not_Reported() =>
        Assert.Null(GpuUseCheck.Verdict("cpu", 0, 99 * Megabyte, out bool _));

    [Fact]
    public void A_CPU_Run_Whose_Counter_Could_Not_Be_Read_Is_Not_Reported() =>
        Assert.Null(GpuUseCheck.Verdict("cpu", -1, 512 * Megabyte, out bool _));

    [Theory]
    [InlineData(0, 4096)]
    [InlineData(-1, 4096)]
    public void An_Auto_Device_Is_Never_Reported(long preLoadMb, long afterLoadMb) =>
        Assert.Null(GpuUseCheck.Verdict("auto", preLoadMb * Megabyte, afterLoadMb * Megabyte, out bool _));

    [Theory]
    [InlineData(LMKit.Global.Runtime.BackendType.Cuda13, "Cuda13")]
    [InlineData(LMKit.Global.Runtime.BackendType.Cuda12, "Cuda12")]
    [InlineData(LMKit.Global.Runtime.BackendType.Vulkan, "Vulkan")]
    public void A_CPU_Run_Whose_Backend_Is_A_GPU_One_Is_An_Error(LMKit.Global.Runtime.BackendType backend, string name)
    {
        string? verdict = GpuUseCheck.BackendVerdict("cpu", backend, out bool isError);

        Assert.True(isError);
        Assert.NotNull(verdict);
        Assert.Contains("CPU mode asked", verdict, StringComparison.Ordinal);
        Assert.Contains(name, verdict, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LMKit.Global.Runtime.BackendType.CPU)]
    [InlineData(LMKit.Global.Runtime.BackendType.Avx)]
    [InlineData(LMKit.Global.Runtime.BackendType.Avx2)]
    public void A_CPU_Run_Whose_Backend_Is_A_CPU_One_Is_Not_Reported(LMKit.Global.Runtime.BackendType backend) =>
        Assert.Null(GpuUseCheck.BackendVerdict("cpu", backend, out bool _));

    [Theory]
    [InlineData("gpu")]
    [InlineData("auto")]
    public void A_GPU_Or_Auto_Run_Is_Not_Reported_Whatever_The_Backend(string device) =>
        Assert.Null(GpuUseCheck.BackendVerdict(device, LMKit.Global.Runtime.BackendType.Cuda13, out bool _));
}
