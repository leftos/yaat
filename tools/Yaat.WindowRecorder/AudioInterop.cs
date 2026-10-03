using System.Runtime.InteropServices;

namespace Yaat.WindowRecorder;

/// <summary>
/// The COM seams of process-loopback audio capture: activating an audio client on the virtual process-loopback device for
/// one process tree, and the WASAPI client and capture-client interfaces the stream is read through.
/// </summary>
internal static class AudioInterop
{
    public const int ShareModeShared = 0;
    public const uint StreamFlagsLoopback = 0x0002_0000;
    public const uint StreamFlagsEventCallback = 0x0004_0000;
    public const uint StreamFlagsAutoConvertPcm = 0x8000_0000;
    public const uint BufferFlagsSilent = 0x2;
    public static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private const string ProcessLoopbackDevice = @"VAD\Process_Loopback";
    private const ushort VariantTypeBlob = 65;
    private const int ActivationTypeProcessLoopback = 1;
    private const int LoopbackModeIncludeTargetProcessTree = 0;
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly TimeSpan ActivationWait = TimeSpan.FromSeconds(10);

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, in WaveFormatEx format, nint audioSessionGuid);

        uint GetBufferSize();

        long GetStreamLatency();

        uint GetCurrentPadding();

        [PreserveSig]
        int IsFormatSupported(int shareMode, in WaveFormatEx format, out nint closestMatch);

        nint GetMixFormat();

        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        void Start();

        void Stop();

        void Reset();

        void SetEventHandle(nint eventHandle);

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetService(in Guid iid);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        void GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong performanceCounterPosition);

        void ReleaseBuffer(uint frames);

        uint GetNextPacketSize();
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    /// <summary>Marks the completion handler as callable from any apartment; activation refuses a handler without it.</summary>
    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    /// <summary>
    /// Activates an audio client that captures what the process <paramref name="processId"/> and its descendants render,
    /// whichever output device they render to. Throws <see cref="AudioActivationException"/> when activation fails.
    /// </summary>
    public static IAudioClient ActivateProcessLoopback(int processId)
    {
        ActivationParams parameters = new()
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = LoopbackModeIncludeTargetProcessTree,
        };
        int parametersSize = Marshal.SizeOf<ActivationParams>();
        nint parametersBlock = Marshal.AllocHGlobal(parametersSize);
        try
        {
            Marshal.StructureToPtr(parameters, parametersBlock, fDeleteOld: false);
            PropVariantBlob variant = new()
            {
                VariantType = VariantTypeBlob,
                Size = (uint)parametersSize,
                Data = parametersBlock,
            };
            using CompletionHandler handler = new();
            IActivateAudioInterfaceAsyncOperation operation = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, AudioClientIid, variant, handler);
            if (!handler.Wait(ActivationWait))
            {
                throw new AudioActivationException($"activation did not complete in {ActivationWait.TotalSeconds} s");
            }
            operation.GetActivateResult(out int result, out object? activated);
            if (result < 0 || activated is not IAudioClient client)
            {
                throw new AudioActivationException(FormattableString.Invariant($"activation failed with HRESULT 0x{result:X8}"));
            }
            return client;
        }
        catch (COMException failure)
        {
            throw new AudioActivationException(FormattableString.Invariant($"activation failed with HRESULT 0x{failure.HResult:X8}"), failure);
        }
        finally
        {
            Marshal.FreeHGlobal(parametersBlock);
        }
    }

    [DllImport("mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IActivateAudioInterfaceAsyncOperation ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        in Guid iid,
        in PropVariantBlob activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler
    );

    /// <summary>WAVEFORMATEX: 18 bytes, packed.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormatEx
    {
        private const ushort FormatPcm = 1;

        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;

        public static WaveFormatEx Pcm(int samplesPerSecond, int channels, int bitsPerSample)
        {
            int blockAlign = channels * bitsPerSample / 8;
            return new WaveFormatEx
            {
                FormatTag = FormatPcm,
                Channels = (ushort)channels,
                SamplesPerSecond = (uint)samplesPerSecond,
                AverageBytesPerSecond = (uint)(samplesPerSecond * blockAlign),
                BlockAlign = (ushort)blockAlign,
                BitsPerSample = (ushort)bitsPerSample,
                ExtraSize = 0,
            };
        }
    }

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS with its process-loopback member.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>A PROPVARIANT holding a VT_BLOB: the type tag, three reserved words, then the BLOB's size and pointer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint Size;
        public nint Data;
    }

    /// <summary>Activation completes on a worker thread; the handler only signals, and the caller reads the result.</summary>
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject, IDisposable
    {
        private readonly ManualResetEventSlim _completed = new();

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => _completed.Set();

        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);

        public void Dispose() => _completed.Dispose();
    }
}
