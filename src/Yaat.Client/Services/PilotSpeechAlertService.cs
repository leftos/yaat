using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Plays a short audible chime when a sim-initiated pilot transmission appears in the
/// terminal pane (TerminalEntryKind.PilotSpeech), gated by the
/// <c>RpoPilotSpeechAudibleAlert</c> user preference.
///
/// The chime is generated in code so the client doesn't ship a WAV asset.
/// Two-tone bell: ~880 Hz (A5) → ~660 Hz (E5), 120 ms each, with
/// a fast attack and a 100 ms exponential decay tail to avoid click on cutoff. Mono, 44.1 kHz,
/// Float32 PCM. The cached buffer is built lazily on first play and reused.
///
/// Playback uses the same PortAudio output path as pilot voice so the feature remains
/// cross-platform. Failures (no audio device, device busy) are caught and logged at Warning —
/// the ding is best-effort by design, never blocking.
/// </summary>
public sealed class PilotSpeechAlertService
{
    private static readonly ILogger Log = AppLog.CreateLogger<PilotSpeechAlertService>();

    private const int SampleRate = 44100;
    private const float OutputVolume = 0.5f;

    private readonly object _lock = new();
    private readonly PortAudioFloatPlayer _player;
    private float[]? _cachedDing;

    public PilotSpeechAlertService(UserPreferences preferences)
    {
        _player = new PortAudioFloatPlayer(preferences);
    }

    public void PlayDing()
    {
        _ = PlayDingAsync();
    }

    private async Task PlayDingAsync()
    {
        try
        {
            var ding = EnsureCachedDing();
            await _player.PlayAsync(ding, SampleRate, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to play pilot-speech ding");
        }
    }

    private float[] EnsureCachedDing()
    {
        if (_cachedDing is not null)
        {
            return _cachedDing;
        }

        lock (_lock)
        {
            _cachedDing ??= GenerateDing();
            return _cachedDing;
        }
    }

    private static float[] GenerateDing()
    {
        // Two-tone notification: A5 → E5, 120 ms each, with attack + decay envelope so each
        // tone has a distinct "bell" character instead of a flat blip. Total ≈ 240 ms.
        var firstTone = BuildTone(880.0, durationMs: 120, attackMs: 5, decayMs: 100);
        var secondTone = BuildTone(660.0, durationMs: 120, attackMs: 5, decayMs: 100);

        int totalSamples = firstTone.Length + secondTone.Length;
        var samples = new float[totalSamples];
        Array.Copy(firstTone, 0, samples, 0, firstTone.Length);
        Array.Copy(secondTone, 0, samples, firstTone.Length, secondTone.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= OutputVolume;
        }

        return samples;
    }

    private static float[] BuildTone(double frequencyHz, int durationMs, int attackMs, int decayMs)
    {
        int sampleCount = (int)(SampleRate * durationMs / 1000.0);
        int attackSamples = Math.Max(1, (int)(SampleRate * attackMs / 1000.0));
        int decayStart = Math.Max(0, sampleCount - (int)(SampleRate * decayMs / 1000.0));

        var samples = new float[sampleCount];
        double phaseStep = 2.0 * Math.PI * frequencyHz / SampleRate;
        for (int i = 0; i < sampleCount; i++)
        {
            double envelope = 1.0;
            if (i < attackSamples)
            {
                envelope = (double)i / attackSamples;
            }
            else if (i >= decayStart)
            {
                int decaySample = i - decayStart;
                int decayLength = sampleCount - decayStart;
                // Exponential decay so the tail rolls off cleanly without click.
                envelope = Math.Exp(-3.0 * decaySample / decayLength);
            }

            samples[i] = (float)(Math.Sin(phaseStep * i) * envelope);
        }

        return samples;
    }
}
