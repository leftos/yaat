using System.Runtime.InteropServices;
using SherpaOnnx;

namespace Yaat.Client.Services.Speech;

/// <summary>
/// The process's binding to sherpa-onnx's C API for the offline transducer engines. It lives in this assembly because
/// <see cref="SherpaNativeLoader"/> registers its CUDA resolver on it, so a recognizer built here runs on the set the
/// loader picked.
/// </summary>
public sealed partial class SherpaOfflineNativeApi : ISherpaOfflineApi
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

    // OfflineRecognizerConfig (the SherpaOnnx package's struct) holds string fields, which source-generated marshalling
    // cannot pass without a custom marshaller, so this one keeps the runtime's built-in struct marshalling.
#pragma warning disable SYSLIB1054
    [DllImport(NativeLibrary)]
    private static extern IntPtr SherpaOnnxCreateOfflineRecognizer(ref OfflineRecognizerConfig config);
#pragma warning restore SYSLIB1054

    [LibraryImport(NativeLibrary)]
    private static partial void SherpaOnnxDestroyOfflineRecognizer(IntPtr recognizer);

    [LibraryImport(NativeLibrary)]
    private static partial IntPtr SherpaOnnxCreateOfflineStream(IntPtr recognizer);

    [LibraryImport(NativeLibrary)]
    private static partial IntPtr SherpaOnnxCreateOfflineStreamWithHotwords(IntPtr recognizer, [MarshalAs(UnmanagedType.LPUTF8Str)] string hotwords);

    [LibraryImport(NativeLibrary)]
    private static partial void SherpaOnnxOfflineStreamSetOption(
        IntPtr stream,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value
    );

    [LibraryImport(NativeLibrary)]
    private static partial void SherpaOnnxAcceptWaveformOffline(IntPtr stream, int sampleRate, float[] samples, int n);

    [LibraryImport(NativeLibrary)]
    private static partial void SherpaOnnxDecodeOfflineStream(IntPtr recognizer, IntPtr stream);

    [LibraryImport(NativeLibrary)]
    private static partial IntPtr SherpaOnnxGetOfflineStreamResultAsJson(IntPtr stream);

    [LibraryImport(NativeLibrary)]
    private static partial void SherpaOnnxDestroyOfflineStreamResultJson(IntPtr json);

    [LibraryImport(NativeLibrary)]
    private static partial void SherpaOnnxDestroyOfflineStream(IntPtr stream);
}
