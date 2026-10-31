using System.Text;
using Microsoft.Extensions.Logging;
using PiperPhonemize;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Phonemizes TTS text into the codepoints sherpa-onnx synthesizes from, using the piper-phonemize
/// natives its package carries. sherpa-onnx 2.0 removed espeak-ng from its own native, so a Piper,
/// Kitten or Matcha model there has no text frontend and renders silence; the caller passes the
/// result as <c>OfflineTtsGenerationConfig.PhonemeCodepoints</c> and synthesizes an empty string.
/// espeak-ng is process-global, so the first call initializes it from one directory for the life of
/// the process.
/// </summary>
public static class EspeakPhonemizer
{
    /// <summary>The espeak-ng voice used when a model carries no language metadata.</summary>
    public const string DefaultVoice = "en-us";

    private static readonly ILogger Log = AppLog.CreateLogger(nameof(EspeakPhonemizer));
    private static readonly Lock Gate = new();

    private static string? _initializedDir;

    /// <summary>
    /// Phonemizes <paramref name="text"/> into one codepoint array per spoken sentence, using the
    /// espeak-ng data directory at <paramref name="espeakDataDir"/> and the espeak-ng voice
    /// <paramref name="voice"/>. Sentences that phonemize to nothing are skipped.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The directory is not an espeak-ng data directory.</exception>
    /// <exception cref="InvalidOperationException">
    /// espeak-ng failed to initialize, a different directory is already initialized in this process, or
    /// piper-phonemize returned no result.
    /// </exception>
    /// <exception cref="InvalidDataException">No sentence produced any phonemes.</exception>
    public static int[][] Phonemize(string text, string voice, string espeakDataDir)
    {
        string fullPath = Path.GetFullPath(espeakDataDir);
        if (!File.Exists(Path.Combine(fullPath, "phontab")))
        {
            throw new DirectoryNotFoundException($"espeak-ng data directory has no phontab: {fullPath}");
        }

        lock (Gate)
        {
            _initializedDir ??= Initialize(fullPath);
            if (!string.Equals(_initializedDir, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"espeak-ng is process-global and already initialized from {_initializedDir}; cannot phonemize from {fullPath}."
                );
            }

            return PhonemizeLocked(text, voice);
        }
    }

    /// <summary>The espeak-ng voice for a model's language metadata, falling back to <see cref="DefaultVoice"/>.</summary>
    public static string VoiceFor(string modelLang)
    {
        if (!string.IsNullOrEmpty(modelLang))
        {
            return modelLang;
        }

        Log.LogWarning("TTS model has no language metadata; phonemizing as {Voice}.", DefaultVoice);
        return DefaultVoice;
    }

    /// <summary>
    /// Returns <paramref name="samples"/> when they carry audio. Synthesis that yields nothing throws
    /// rather than reaching playback as silence.
    /// </summary>
    /// <exception cref="InvalidDataException">Every sample is zero.</exception>
    public static float[] RequireSpeech(float[] samples, string what)
    {
        foreach (float sample in samples)
        {
            if (sample != 0f)
            {
                return samples;
            }
        }

        throw new InvalidDataException($"{what} produced no speech ({samples.Length} samples).");
    }

    private static string Initialize(string fullPath)
    {
        int sampleRate = PiperPhonemizeApi.Initialize(fullPath);
        if (sampleRate < 0)
        {
            throw new InvalidOperationException($"piper-phonemize failed to initialize from {fullPath}.");
        }

        Log.LogInformation("Initialized piper-phonemize from {EspeakDataDir}; sample rate {SampleRate} Hz.", fullPath, sampleRate);
        return fullPath;
    }

    private static int[][] PhonemizeLocked(string text, string voice)
    {
        using PhonemizeResult result =
            PiperPhonemizeApi.Text(text, voice)
            ?? throw new InvalidOperationException($"piper-phonemize returned no result for voice {voice}: \"{text}\"");

        List<int[]> sentences = [];
        for (int i = 0; i < result.NumSentences; i++)
        {
            int[] codepoints = ToCodepoints(result.GetPhonemesAsString(i) ?? "");
            if (codepoints.Length > 0)
            {
                sentences.Add(codepoints);
            }
        }

        if (sentences.Count == 0)
        {
            throw new InvalidDataException($"piper-phonemize produced no phonemes for voice {voice}: \"{text}\"");
        }

        return [.. sentences];
    }

    private static int[] ToCodepoints(string phonemes)
    {
        List<int> codepoints = [];
        foreach (Rune rune in phonemes.EnumerateRunes())
        {
            codepoints.Add(rune.Value);
        }

        return [.. codepoints];
    }
}
