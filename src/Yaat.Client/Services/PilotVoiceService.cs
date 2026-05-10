using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Dsp;
using PortAudioSharp;
using SherpaOnnx;
using Yaat.Client.Logging;
using Yaat.Sim;

namespace Yaat.Client.Services;

public sealed record PilotVoiceRequest(string Callsign, string Text, int SpeakerId, int Volume, bool RadioFxEnabled);

public interface IPilotVoiceSynthesizer
{
    bool IsAvailable { get; }
    Task SpeakAsync(PilotVoiceRequest request, CancellationToken ct);
}

public sealed class PilotVoiceService : IAsyncDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<PilotVoiceService>();

    private readonly Channel<PilotVoiceRequest> _queue = Channel.CreateUnbounded<PilotVoiceRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
    );
    private readonly IPilotVoiceSynthesizer _synthesizer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public PilotVoiceService(UserPreferences preferences)
        : this(new SherpaOnnxPilotVoiceSynthesizer(preferences)) { }

    internal PilotVoiceService(IPilotVoiceSynthesizer synthesizer)
    {
        _synthesizer = synthesizer;
        _worker = Task.Run(RunAsync);
    }

    public bool IsAvailable => _synthesizer.IsAvailable;

    public void Enqueue(PilotTransmissionBroadcastDto dto, int volume, bool radioFxEnabled)
    {
        if (!_synthesizer.IsAvailable || string.IsNullOrWhiteSpace(dto.Text))
        {
            return;
        }

        Log.LogInformation(
            "[Pilot TTS] {Callsign} source={SourceKind} speaker={SpeakerId}: {Text}",
            dto.Callsign,
            dto.SourceKind,
            dto.SpeakerId,
            dto.Text
        );
        _queue.Writer.TryWrite(new PilotVoiceRequest(dto.Callsign, dto.Text, dto.SpeakerId, volume, radioFxEnabled));
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await _synthesizer.SpeakAsync(request, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Log.LogWarning(ex, "Failed to speak pilot transmission for {Callsign}", request.Callsign);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}

internal sealed class SherpaOnnxPilotVoiceSynthesizer : IPilotVoiceSynthesizer
{
    private static readonly ILogger Log = AppLog.CreateLogger<SherpaOnnxPilotVoiceSynthesizer>();

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly PortAudioFloatPlayer _player;
    private OfflineTts? _tts;
    private string? _loadedVoiceDir;

    public SherpaOnnxPilotVoiceSynthesizer(UserPreferences preferences)
    {
        _player = new PortAudioFloatPlayer(preferences);
    }

    public bool IsAvailable => TryFindVoiceDir() is not null && PortAudioFloatPlayer.HasDefaultOutputDevice();

    public async Task SpeakAsync(PilotVoiceRequest request, CancellationToken ct)
    {
        var tts = await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (tts is null)
        {
            return;
        }

        var (samples, sampleRate) = await Task.Run(
                () =>
                {
                    var sid = Math.Abs(request.SpeakerId) % 904;
                    var gen = new OfflineTtsGenerationConfig { Sid = sid, Speed = 1.0f };
                    var sw = Stopwatch.StartNew();
                    var audio = tts.GenerateWithConfig(request.Text, gen, null);
                    sw.Stop();
                    Log.LogDebug(
                        "Synthesized pilot voice for {Callsign}: sid={Sid}, samples={Samples}, sampleRate={SampleRate}, latencyMs={LatencyMs}",
                        request.Callsign,
                        sid,
                        audio.Samples.Length,
                        tts.SampleRate,
                        sw.ElapsedMilliseconds
                    );
                    return (audio.Samples, tts.SampleRate);
                },
                ct
            )
            .ConfigureAwait(false);

        if (request.RadioFxEnabled)
        {
            samples = RadioAudioFx.Apply(samples, sampleRate);
        }

        float gain = Math.Clamp(request.Volume, 0, 100) / 100f;
        if (gain < 0.999f)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }

        await _player.PlayAsync(samples, sampleRate, ct).ConfigureAwait(false);
    }

    private async Task<OfflineTts?> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_tts is not null)
        {
            return _tts;
        }

        await _loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tts is not null)
            {
                return _tts;
            }

            var voiceDir = TryFindVoiceDir();
            if (voiceDir is null)
            {
                Log.LogInformation("Pilot voice pack is not installed; pilot voice remains silent.");
                return null;
            }

            var dirInfo = new DirectoryInfo(voiceDir);
            var onnxFile = dirInfo.GetFiles("*.onnx").FirstOrDefault();
            var tokensPath = Path.Combine(voiceDir, PilotVoicePack.TokensFileName);
            var dataDir = Path.Combine(voiceDir, PilotVoicePack.EspeakDataDirectoryName);
            if (onnxFile is null || !File.Exists(tokensPath) || !Directory.Exists(dataDir))
            {
                Log.LogWarning("Pilot voice pack at {VoiceDir} is incomplete.", voiceDir);
                return null;
            }

            _tts = await Task.Run(
                    () =>
                    {
                        var config = new OfflineTtsConfig();
                        config.Model.Vits.Model = onnxFile.FullName;
                        config.Model.Vits.Tokens = tokensPath;
                        config.Model.Vits.DataDir = dataDir;
                        config.Model.Vits.LengthScale = 1.0f;
                        config.Model.NumThreads = 2;
                        config.Model.Provider = "cpu";
                        config.Model.Debug = 0;
                        return new OfflineTts(config);
                    },
                    ct
                )
                .ConfigureAwait(false);

            _loadedVoiceDir = voiceDir;
            Log.LogInformation("Loaded pilot voice pack from {VoiceDir}; sample rate {SampleRate} Hz.", _loadedVoiceDir, _tts.SampleRate);
            _ = Task.Run(() => Prewarm(_tts), CancellationToken.None);
            return _tts;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static void Prewarm(OfflineTts tts)
    {
        try
        {
            var gen = new OfflineTtsGenerationConfig { Sid = 0, Speed = 1.0f };
            _ = tts.GenerateWithConfig("warmup", gen, null);
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Pilot voice prewarm failed.");
        }
    }

    private static string? TryFindVoiceDir()
    {
        return PilotVoicePack.FindInstalledDirectory();
    }
}

internal static class RadioAudioFx
{
    public static float[] Apply(float[] input, int sampleRate)
    {
        const float bpCenter = 1450f;
        const float bpQ = 0.9f;
        const float drive = 1.8f;
        const int squelchMs = 120;

        var bp1 = BiQuadFilter.BandPassFilterConstantPeakGain(sampleRate, bpCenter, bpQ);
        var bp2 = BiQuadFilter.BandPassFilterConstantPeakGain(sampleRate, bpCenter, bpQ);
        var hp = BiQuadFilter.HighPassFilter(sampleRate, 200f, 0.7f);

        int squelchTailSamples = (int)(sampleRate * (squelchMs / 1000.0));
        var output = new float[input.Length + squelchTailSamples];

        for (int i = 0; i < input.Length; i++)
        {
            float x = input[i];
            x = hp.Transform(x);
            x = bp1.Transform(x);
            x = bp2.Transform(x);
            x *= drive;
            x = (float)Math.Tanh(x);
            output[i] = x * 0.85f;
        }

        if (squelchTailSamples > 0)
        {
            var rng = new Random(42);
            for (int i = 0; i < squelchTailSamples; i++)
            {
                float t = i / (float)squelchTailSamples;
                float envelope = (1f - t) * 0.04f;
                float noise = (float)((rng.NextDouble() * 2.0) - 1.0);
                noise = bp1.Transform(noise);
                output[input.Length + i] = noise * envelope;
            }
        }

        return output;
    }
}

internal sealed class PortAudioFloatPlayer
{
    private static readonly ILogger Log = AppLog.CreateLogger<PortAudioFloatPlayer>();
    private static readonly object InitLock = new();
    private static bool _initialized;

    private readonly UserPreferences _preferences;

    public PortAudioFloatPlayer(UserPreferences preferences)
    {
        _preferences = preferences;
    }

    public static bool HasDefaultOutputDevice()
    {
        try
        {
            EnsureInitialized();
            return PortAudio.DefaultOutputDevice != PortAudio.NoDevice;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "PortAudio output device check failed.");
            return false;
        }
    }

    public async Task PlayAsync(float[] samples, int sampleRate, CancellationToken ct)
    {
        EnsureInitialized();

        int outDev = ResolveOutputDevice(_preferences.AudioOutputDevice);
        if (outDev == PortAudio.NoDevice)
        {
            return;
        }

        var devInfo = PortAudio.GetDeviceInfo(outDev);
        var outParams = new StreamParameters
        {
            device = outDev,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = devInfo.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        int cursor = 0;
        StreamCallbackResult Callback(
            IntPtr input,
            IntPtr output,
            uint frameCount,
            ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags,
            IntPtr userData
        )
        {
            if (ct.IsCancellationRequested)
            {
                ZeroFill(output, (int)frameCount);
                return StreamCallbackResult.Complete;
            }

            int remaining = samples.Length - cursor;
            int toCopy = (int)Math.Min(frameCount, remaining);
            if (toCopy > 0)
            {
                Marshal.Copy(samples, cursor, output, toCopy);
                cursor += toCopy;
            }

            if (toCopy < frameCount)
            {
                var zeroDest = IntPtr.Add(output, toCopy * sizeof(float));
                ZeroFill(zeroDest, (int)frameCount - toCopy);
                return StreamCallbackResult.Complete;
            }

            return StreamCallbackResult.Continue;
        }

        using var stream = new PortAudioSharp.Stream(
            inParams: null,
            outParams: outParams,
            sampleRate: sampleRate,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.ClipOff,
            callback: Callback,
            userData: IntPtr.Zero
        );

        stream.Start();
        try
        {
            var duration = TimeSpan.FromSeconds(samples.Length / (double)sampleRate + 0.3);
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            try
            {
                stream.Stop();
            }
            catch
            {
                // Shutdown is best-effort; the stream is disposed immediately after.
            }
        }
    }

    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            PortAudio.Initialize();
            _initialized = true;
        }
    }

    private static int ResolveOutputDevice(string preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred))
        {
            return PortAudio.DefaultOutputDevice;
        }

        // Match by exact name first, then fall back to a case-insensitive substring match so users
        // can type "Headset" and get "Realtek USB Audio Headset" without copy-pasting the full name.
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels > 0 && string.Equals(info.name, preferred, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels > 0 && info.name.Contains(preferred, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        Log.LogWarning("Preferred output device '{Preferred}' not found; falling back to default", preferred);
        return PortAudio.DefaultOutputDevice;
    }

    private static void ZeroFill(IntPtr dest, int floatCount)
    {
        var zeros = new float[floatCount];
        Marshal.Copy(zeros, 0, dest, floatCount);
    }
}
