using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>What <see cref="NativeStderrCapture.CudaFallbackReasonFrom"/> reads out of sherpa-onnx's CUDA fallback line.</summary>
public sealed class NativeStderrCaptureTests
{
    private const string Providers = ". Available providers: TensorrtExecutionProvider, CUDAExecutionProvider, CPUExecutionProvider. Fallback to cpu";

    /// <summary>The second line Windows prints: ONNX Runtime's status ends in a newline, which splits sherpa-onnx's line.</summary>
    private const string MeasuredSecondLine =
        ". Available providers: TensorrtExecutionProvider, CUDAExecutionProvider, CPUExecutionProvider, . Fallback to cpu";

    [Theory]
    [InlineData(@"Error loading ""C:\y\onnxruntime_providers_cuda.dll"" which is missing. (Error 193: ""%1 is not a valid Win32 application."")")]
    [InlineData(@"Failed to parse provider option ""device_id"": CUDA failure 100: no CUDA-capable device is detected")]
    [InlineData(@"Error loading ""C:\y\onnxruntime_providers_cuda.dll"" which depends on ""cublasLt64_13.dll"" which is missing. (Error 126)")]
    public void CudaFallbackReasonFrom_TheFallbackLine_IsTheOrtStatus(string status)
    {
        string line = $@"C:\sherpa-onnx\csrc\session.cc:SetProvider:198 Failed to enable CUDA: {status}{Providers}";

        Assert.Equal(status, NativeStderrCapture.CudaFallbackReasonFrom(line));
    }

    [Fact]
    public void CudaFallbackReasonFrom_MeasuredWindowsLine_IsTheBareOrtStatus()
    {
        string line =
            @"D:\a\sherpa-onnx\sherpa-onnx\sherpa-onnx\csrc\session.cc:AppendCudaProviderV2:200 Failed to enable CUDA: "
            + @"N:\_work\1\s\onnxruntime\core\session\provider_bridge_ort.cc:1988 onnxruntime::ProviderLibrary::Get "
            + @"[ONNXRuntimeError] : 1 : FAIL : "
            + @"Error loading ""X:\y\onnxruntime_providers_cuda.dll"" which is missing. (Error 193: ""%1 is not a valid Win32 application."")";

        Assert.Equal(
            @"Error loading ""X:\y\onnxruntime_providers_cuda.dll"" which is missing. (Error 193: ""%1 is not a valid Win32 application."")",
            NativeStderrCapture.CudaFallbackReasonFrom(line)
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-11-02 10:00:00.000 [E:onnxruntime:, provider_bridge_ort.cc:2253] LoadLibrary failed with error 126")]
    [InlineData("Failed to enable TensorRT: something. Fallback to cpu")]
    [InlineData(MeasuredSecondLine)]
    public void CudaFallbackReasonFrom_AnyOtherLine_IsNull(string line) => Assert.Null(NativeStderrCapture.CudaFallbackReasonFrom(line));

    [Fact]
    public void CudaFallbackReasonFrom_NoProvidersSeparator_ReadsUpToFallbackToCpu() =>
        Assert.Equal(
            "CUDA failure 100: no CUDA-capable device is detected",
            NativeStderrCapture.CudaFallbackReasonFrom("Failed to enable CUDA: CUDA failure 100: no CUDA-capable device is detected  Fallback to cpu")
        );
}
