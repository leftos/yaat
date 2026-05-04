using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Plays a short audible chime when a sim-initiated pilot transmission appears in the
/// terminal pane (TerminalEntryKind.PilotSpeech), gated by the
/// <c>RpoPilotSpeechAudibleAlert</c> user preference.
///
/// The chime is generated in code via NAudio's <see cref="SignalGenerator"/> so the client
/// doesn't ship a WAV asset. Two-tone bell: ~880 Hz (A5) → ~660 Hz (E5), 120 ms each, with
/// a fast attack and a 100 ms exponential decay tail to avoid click on cutoff. Mono, 44.1 kHz,
/// 16-bit PCM. The cached PCM buffer is built lazily on first play (≈30 KB) and reused.
///
/// Each <see cref="PlayDing"/> call constructs a fresh <see cref="WaveOutEvent"/> so overlapping
/// transmissions can layer without one cutting off the previous; the device disposes itself when
/// playback completes via <see cref="WaveOutEvent.PlaybackStopped"/>. Failures (no audio device,
/// device busy) are caught and logged at Warning — the ding is best-effort by design, never blocking.
/// </summary>
public sealed class PilotSpeechAlertService
{
    private static readonly ILogger Log = AppLog.CreateLogger<PilotSpeechAlertService>();

    private const int SampleRate = 44100;
    private const float OutputVolume = 0.5f;

    private readonly object _lock = new();
    private byte[]? _cachedDingPcm;

    public void PlayDing()
    {
        try
        {
            var pcm = EnsureCachedDing();
            var ms = new System.IO.MemoryStream(pcm, writable: false);
            var reader = new RawSourceWaveStream(ms, new WaveFormat(SampleRate, 16, 1));
            var output = new WaveOutEvent { Volume = OutputVolume };
            output.Init(reader);
            output.PlaybackStopped += (_, _) =>
            {
                output.Dispose();
                reader.Dispose();
                ms.Dispose();
            };
            output.Play();
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to play pilot-speech ding");
        }
    }

    private byte[] EnsureCachedDing()
    {
        if (_cachedDingPcm is not null)
        {
            return _cachedDingPcm;
        }

        lock (_lock)
        {
            _cachedDingPcm ??= GenerateDing();
            return _cachedDingPcm;
        }
    }

    private static byte[] GenerateDing()
    {
        // Two-tone notification: A5 → E5, 120 ms each, with attack + decay envelope so each
        // tone has a distinct "bell" character instead of a flat blip. Total ≈ 240 ms.
        var firstTone = BuildTone(880.0, durationMs: 120, attackMs: 5, decayMs: 100);
        var secondTone = BuildTone(660.0, durationMs: 120, attackMs: 5, decayMs: 100);

        int totalSamples = firstTone.Length + secondTone.Length;
        var pcm = new byte[totalSamples * 2];
        int byteIndex = 0;
        WriteSamples(firstTone, pcm, ref byteIndex);
        WriteSamples(secondTone, pcm, ref byteIndex);
        return pcm;
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

    private static void WriteSamples(float[] samples, byte[] dest, ref int byteIndex)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            // Clamp before scaling to int16 range; full-scale is ±32767.
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short pcm16 = (short)(clamped * 32767);
            dest[byteIndex++] = (byte)(pcm16 & 0xFF);
            dest[byteIndex++] = (byte)((pcm16 >> 8) & 0xFF);
        }
    }
}
