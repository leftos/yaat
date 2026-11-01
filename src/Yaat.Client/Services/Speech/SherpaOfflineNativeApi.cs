using System.Runtime.InteropServices;
using SherpaOnnx;

namespace Yaat.Client.Services.Speech;

/// <summary>
/// The process's binding to sherpa-onnx's C API for the offline transducer engines. It lives in this assembly because
/// <see cref="SherpaNativeLoader"/> registers its CUDA resolver on it, so a recognizer built here runs on the set the
/// loader picked.
/// </summary>
public sealed class SherpaOfflineNativeApi : ISherpaOfflineApi
{
    private const string NativeLibrary = "sherpa-onnx-c-api";

    private SherpaOfflineNativeApi() { }

    /// <summary>The process's instance; the native loads once, so every engine shares it.</summary>
    public static SherpaOfflineNativeApi Instance { get; } = new();

    /// <inheritdoc />
    public IntPtr CreateRecognizer(ref OfflineRecognizerConfig config) => SherpaOnnxCreateOfflineRecognizer(ref config);

    /// <inheritdoc />
    public void DestroyRecognizer(IntPtr recognizer) => SherpaOnnxDestroyOfflineRecognizer(recognizer);

    /// <inheritdoc />
    public IntPtr CreateStream(IntPtr recognizer, string hotwords) =>
        (hotwords.Length > 0) ? SherpaOnnxCreateOfflineStreamWithHotwords(recognizer, hotwords) : SherpaOnnxCreateOfflineStream(recognizer);

    /// <inheritdoc />
    public void SetStreamOption(IntPtr stream, string key, string value) => SherpaOnnxOfflineStreamSetOption(stream, key, value);

    /// <inheritdoc />
    public void AcceptWaveform(IntPtr stream, int sampleRate, float[] samples) =>
        SherpaOnnxAcceptWaveformOffline(stream, sampleRate, samples, samples.Length);

    /// <inheritdoc />
    public void Decode(IntPtr recognizer, IntPtr stream) => SherpaOnnxDecodeOfflineStream(recognizer, stream);

    /// <summary>The stream's result; the native writes UTF-8 and the tokens carry non-ASCII word markers.</summary>
    public string? ResultJson(IntPtr stream)
    {
        IntPtr json = SherpaOnnxGetOfflineStreamResultAsJson(stream);
        if (json == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(json);
        }
        finally
        {
            SherpaOnnxDestroyOfflineStreamResultJson(json);
        }
    }

    /// <inheritdoc />
    public void DestroyStream(IntPtr stream) => SherpaOnnxDestroyOfflineStream(stream);

    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxCreateOfflineRecognizer(ref OfflineRecognizerConfig config);

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxDestroyOfflineRecognizer(IntPtr recognizer);

    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxCreateOfflineStream(IntPtr recognizer);

    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxCreateOfflineStreamWithHotwords(IntPtr recognizer, [MarshalAs(UnmanagedType.LPUTF8Str)] string hotwords);

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxOfflineStreamSetOption(
        IntPtr stream,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value
    );

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxAcceptWaveformOffline(IntPtr stream, int sampleRate, float[] samples, int n);

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxDecodeOfflineStream(IntPtr recognizer, IntPtr stream);

    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxGetOfflineStreamResultAsJson(IntPtr stream);

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxDestroyOfflineStreamResultJson(IntPtr json);

    [DllImport(NativeLibrary)]
    private static extern void SherpaOnnxDestroyOfflineStream(IntPtr stream);
}
