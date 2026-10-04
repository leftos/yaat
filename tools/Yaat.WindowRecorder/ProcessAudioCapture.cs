using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Yaat.WindowRecorder;

/// <summary>
/// Captures what one process tree renders, as 48 kHz stereo 16-bit PCM, at a constant rate. The loopback stream carries
/// nothing while the process is silent, so the gaps are filled with silence by wall-clock time from the start: the output
/// is exactly as long as the run, whatever the process played.
/// </summary>
internal sealed class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int BitsPerSample = 16;
    public const int BytesPerFrame = Channels * BitsPerSample / 8;

    /// <summary>How far behind the wall clock the output runs, so a packet the engine delivers late is not replaced by silence.</summary>
    private static readonly TimeSpan LatencySlack = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly AudioInterop.IAudioClient _client;
    private readonly AudioInterop.IAudioCaptureClient _captureClient;
    private readonly AutoResetEvent _samplesReady;

    /// <summary>Activates and initialises the stream without starting it; throws <see cref="AudioActivationException"/> on failure.</summary>
    public ProcessAudioCapture(int processId)
    {
        _client = AudioInterop.ActivateProcessLoopback(processId);
        _samplesReady = new AutoResetEvent(false);
        try
        {
            // The virtual loopback device has no mix format of its own: the caller names the format and the engine
            // converts to it (AUTOCONVERTPCM), as Microsoft's ApplicationLoopback sample does.
            var format = AudioInterop.WaveFormatEx.Pcm(SampleRate, Channels, BitsPerSample);
            uint flags = AudioInterop.StreamFlagsLoopback | AudioInterop.StreamFlagsEventCallback | AudioInterop.StreamFlagsAutoConvertPcm;
            _client.Initialize(AudioInterop.ShareModeShared, flags, 0, 0, format, nint.Zero);
            _client.SetEventHandle(_samplesReady.SafeWaitHandle.DangerousGetHandle());
            _captureClient = (AudioInterop.IAudioCaptureClient)_client.GetService(AudioInterop.AudioCaptureClientIid);
        }
        catch (COMException failure)
        {
            _samplesReady.Dispose();
            throw new AudioActivationException(FormattableString.Invariant($"initialisation failed with HRESULT 0x{failure.HResult:X8}"), failure);
        }
    }

    /// <summary>
    /// Starts the stream and writes audio into <paramref name="sink"/> until <paramref name="deadline"/> passes, then
    /// silence up to that instant with no slack held back, so the track lasts as long as the run; completes the sink at the
    /// end and returns how many of the frames the loopback stream delivered rather than silence filling.
    /// </summary>
    public long Run(Deadline deadline, ChannelWriter<byte[]> sink)
    {
        long written = 0;
        long captured = 0;
        var clock = Stopwatch.StartNew();
        _client.Start();
        try
        {
            while (!deadline.HasPassed())
            {
                _samplesReady.WaitOne(PollInterval);
                long drained = DrainPackets(sink);
                written += drained;
                captured += drained;
                written = FillSilence(sink, written, FramesAt(clock.Elapsed - LatencySlack));
            }
            long last = DrainPackets(sink);
            captured += last;
            FillSilence(sink, written + last, FramesAt(clock.Elapsed));
            return captured;
        }
        finally
        {
            _client.Stop();
            sink.TryComplete();
        }
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(_captureClient);
        Marshal.ReleaseComObject(_client);
        _samplesReady.Dispose();
    }

    private static long FramesAt(TimeSpan elapsed) => elapsed <= TimeSpan.Zero ? 0 : elapsed.Ticks * SampleRate / TimeSpan.TicksPerSecond;

    /// <summary>Writes silence from <paramref name="written"/> frames up to <paramref name="due"/>; returns the new total.</summary>
    private static long FillSilence(ChannelWriter<byte[]> sink, long written, long due)
    {
        if (due <= written)
        {
            return written;
        }
        sink.TryWrite(new byte[(due - written) * BytesPerFrame]);
        return due;
    }

    /// <summary>Reads every packet the engine holds; a packet flagged silent is written as zeros.</summary>
    private long DrainPackets(ChannelWriter<byte[]> sink)
    {
        long emitted = 0;
        while (_captureClient.GetNextPacketSize() > 0)
        {
            _captureClient.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _);
            try
            {
                if (frames > 0)
                {
                    byte[] chunk = new byte[frames * BytesPerFrame];
                    if ((flags & AudioInterop.BufferFlagsSilent) == 0)
                    {
                        Marshal.Copy(data, chunk, 0, chunk.Length);
                    }
                    sink.TryWrite(chunk);
                    emitted += frames;
                }
            }
            finally
            {
                _captureClient.ReleaseBuffer(frames);
            }
        }
        return emitted;
    }
}
