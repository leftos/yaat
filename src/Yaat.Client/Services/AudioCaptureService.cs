using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PortAudioSharp;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Push-to-talk microphone capture via PortAudioSharp2. Records 16 kHz mono Float32 PCM into a
/// growing buffer and returns the full capture as a <see cref="float"/>[] on stop.
///
/// Usage:
/// <list type="number">
///   <item><description><see cref="StartCapture"/> begins recording on the selected input device.</description></item>
///   <item><description>The PortAudio callback (running on a PortAudio-managed thread) appends
///     incoming samples to a lock-guarded buffer.</description></item>
///   <item><description><see cref="StopCapture"/> halts the stream and returns the captured samples.
///     Returns an empty array if the stream never started or nothing was recorded.</description></item>
/// </list>
///
/// PortAudio is initialized lazily on first <see cref="StartCapture"/> and kept alive for the life
/// of the service. Disposal terminates PortAudio globally — only call <see cref="Dispose"/> at app
/// shutdown since there's no way to re-initialize in the same process without side effects.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<AudioCaptureService>();

    // Whisper's native sample rate. All Whisper.net models expect 16 kHz mono PCM float input;
    // we record directly at this rate to avoid resampling.
    public const int SampleRate = 16000;

    private readonly UserPreferences _preferences;
    private readonly object _bufferLock = new();

    private List<float> _capturedSamples = [];
    private PortAudioSharp.Stream? _stream;
    private bool _portAudioInitialized;
    private bool _isCapturing;

    public AudioCaptureService(UserPreferences preferences)
    {
        _preferences = preferences;
    }

    public bool IsCapturing
    {
        get
        {
            lock (_bufferLock)
            {
                return _isCapturing;
            }
        }
    }

    /// <summary>
    /// Starts a new capture on the input device resolved from <see cref="UserPreferences.AudioInputDevice"/>
    /// (or the system default when no preference is set). Any in-progress capture is silently stopped
    /// and its samples discarded. Returns false on device-enumeration or stream-open failures.
    /// </summary>
    public bool StartCapture()
    {
        lock (_bufferLock)
        {
            if (_isCapturing)
            {
                Log.LogWarning("StartCapture called while already capturing; aborting prior capture");
                StopStreamUnsafe();
            }

            try
            {
                EnsurePortAudioInitialized();

                var deviceIndex = ResolveInputDevice(_preferences.AudioInputDevice);
                if (deviceIndex == PortAudio.NoDevice)
                {
                    Log.LogError("No audio input device available");
                    return false;
                }

                var info = PortAudio.GetDeviceInfo(deviceIndex);
                var param = new StreamParameters
                {
                    device = deviceIndex,
                    channelCount = 1,
                    sampleFormat = SampleFormat.Float32,
                    suggestedLatency = info.defaultLowInputLatency,
                    hostApiSpecificStreamInfo = IntPtr.Zero,
                };

                _capturedSamples = [];
                _stream = new PortAudioSharp.Stream(
                    inParams: param,
                    outParams: null,
                    sampleRate: SampleRate,
                    framesPerBuffer: 0,
                    streamFlags: StreamFlags.ClipOff,
                    callback: OnSamplesAvailable,
                    userData: IntPtr.Zero
                );
                _stream.Start();
                _isCapturing = true;
                Log.LogInformation("Audio capture started on device {Index} ({Name})", deviceIndex, info.name);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Failed to start audio capture");
                StopStreamUnsafe();
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the active capture (if any) and returns the captured samples. Returns an empty array
    /// when no capture is active. Thread-safe with the PortAudio callback.
    /// </summary>
    public float[] StopCapture()
    {
        lock (_bufferLock)
        {
            if (!_isCapturing)
            {
                return [];
            }

            StopStreamUnsafe();
            var samples = _capturedSamples.ToArray();
            _capturedSamples = [];
            Log.LogInformation("Audio capture stopped: {SampleCount} samples ({Seconds:F2}s)", samples.Length, samples.Length / (float)SampleRate);
            return samples;
        }
    }

    /// <summary>
    /// Enumerates available input devices as (index, name) pairs. Used by the Settings UI to let
    /// the user pick a mic. Exposed as a method rather than a property because the PortAudio call
    /// requires the library to be initialized, which is a non-trivial side effect.
    /// </summary>
    public IReadOnlyList<(int Index, string Name)> ListInputDevices()
    {
        try
        {
            EnsurePortAudioInitialized();
            var devices = new List<(int, string)>();
            for (var i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0)
                {
                    devices.Add((i, info.name));
                }
            }

            return devices;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to enumerate input devices");
            return [];
        }
    }

    /// <summary>
    /// Enumerates available output devices as (index, name) pairs. Used by the Settings UI's
    /// Audio tab to let the user pick where pilot TTS and the notification chime play. Mirrors
    /// <see cref="ListInputDevices"/> but filters on <c>maxOutputChannels &gt; 0</c>. Initializes
    /// PortAudio as a side effect (same one-shot init the input enumerator uses).
    /// </summary>
    public IReadOnlyList<(int Index, string Name)> ListOutputDevices()
    {
        try
        {
            EnsurePortAudioInitialized();
            var devices = new List<(int, string)>();
            for (var i = 0; i < PortAudio.DeviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxOutputChannels > 0)
                {
                    devices.Add((i, info.name));
                }
            }

            return devices;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to enumerate output devices");
            return [];
        }
    }

    private void EnsurePortAudioInitialized()
    {
        if (_portAudioInitialized)
        {
            return;
        }

        PortAudio.Initialize();
        _portAudioInitialized = true;
        Log.LogInformation("PortAudio initialized: {Version}", PortAudio.VersionInfo.versionText);
    }

    private int ResolveInputDevice(string preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred))
        {
            return PortAudio.DefaultInputDevice;
        }

        // Match by exact name first, then fall back to a case-insensitive substring match so users
        // can type "Rode" and get "Rode NT-USB Mini" without copy-pasting the full name.
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels > 0 && string.Equals(info.name, preferred, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels > 0 && info.name.Contains(preferred, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        Log.LogWarning("Preferred input device '{Preferred}' not found; falling back to default", preferred);
        return PortAudio.DefaultInputDevice;
    }

    // Callback runs on a PortAudio-owned thread. Must hold _bufferLock to append safely, but the
    // lock is fine-grained enough that it doesn't starve the audio thread.
    private StreamCallbackResult OnSamplesAvailable(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData
    )
    {
        if (input == IntPtr.Zero || frameCount == 0)
        {
            return StreamCallbackResult.Continue;
        }

        var samples = new float[frameCount];
        Marshal.Copy(input, samples, 0, (int)frameCount);

        lock (_bufferLock)
        {
            if (_isCapturing)
            {
                _capturedSamples.AddRange(samples);
            }
        }

        return StreamCallbackResult.Continue;
    }

    private void StopStreamUnsafe()
    {
        _isCapturing = false;
        if (_stream is null)
        {
            return;
        }

        try
        {
            _stream.Stop();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Error stopping PortAudio stream");
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Error disposing PortAudio stream");
        }

        _stream = null;
    }

    public void Dispose()
    {
        lock (_bufferLock)
        {
            StopStreamUnsafe();
            if (_portAudioInitialized)
            {
                try
                {
                    PortAudio.Terminate();
                }
                catch (Exception ex)
                {
                    Log.LogWarning(ex, "Error terminating PortAudio");
                }

                _portAudioInitialized = false;
            }
        }
    }
}
