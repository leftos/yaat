using SherpaOnnx;

namespace Yaat.SpeechSandbox;

/// <summary>The runtime a benchmarked TTS model runs on: a sherpa-onnx model family, or Kitten-TTS 2's own engine.</summary>
internal enum TtsRuntime
{
    Vits,
    Kitten,
    Matcha,
    Pocket,
    ZipVoice,
    Supertonic,
    KittenTts2,
}

/// <summary>
/// One voice of a benchmarked model: a speaker id, or (for a cloning model) the Piper LibriTTS-R speaker whose
/// reference clip the model clones. Kitten-TTS 2 voices are selected by <see cref="Label"/>.
/// </summary>
internal sealed record TtsBenchVoice(string Label, int Sid, int? ReferenceSpeaker);

/// <summary>A benchmarked model: id, display name, runtime, sherpa-onnx release asset (null for Kitten-TTS 2), licence and voices.</summary>
internal sealed record TtsBenchModel(string Id, string Name, TtsRuntime Runtime, string? Asset, string License, IReadOnlyList<TtsBenchVoice> Voices)
{
    public bool IsCloning => Voices.Any(v => v.ReferenceSpeaker is not null);
}

/// <summary>
/// The <c>--tts-bench</c> model manifest and the sherpa-onnx config each model loads with. Archives come from the
/// sherpa-onnx <c>tts-models</c> GitHub release; the file names inside each archive were read from the archives.
/// </summary>
internal static class TtsBenchModels
{
    public const string ModelBaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/";
    public const string VocoderBaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/vocoder-models/";

    /// <summary>Matcha's vocoder (22.05 kHz).</summary>
    public const string Vocos22k = "vocos-22khz-univ.onnx";

    /// <summary>ZipVoice's vocoder (24 kHz); the ZipVoice archive does not carry one.</summary>
    public const string Vocos24k = "vocos_24khz.onnx";

    /// <summary>The model whose speakers read the cloning references.</summary>
    public const string ReferenceModelId = "piper-libritts-r-fp32";

    public const int ProductionNumThreads = 2;

    private const string PiperLicense = "CC BY 4.0 (LibriTTS-R voices; Piper code MIT)";
    private const string KittenLicense = "Apache-2.0 (LICENSE in the archive)";

    private static readonly TtsBenchVoice[] PiperVoices = [new("speaker-10", 10, null), new("speaker-300", 300, null), new("speaker-600", 600, null)];

    // Kitten 0.8's voices.bin holds 8 style rows and the archive names none of them; sids 0, 2 and 4 are the male
    // expr-voice-2-m, expr-voice-3-m and expr-voice-4-m rows in sherpa-onnx's Kitten ordering.
    private static readonly TtsBenchVoice[] KittenVoices =
    [
        new("expr-voice-2-m", 0, null),
        new("expr-voice-3-m", 2, null),
        new("expr-voice-4-m", 4, null),
    ];

    private static readonly TtsBenchVoice[] CloneVoices =
    [
        new("clone-speaker-10", 0, 10),
        new("clone-speaker-300", 0, 300),
        new("clone-speaker-600", 0, 600),
    ];

    public static readonly IReadOnlyList<TtsBenchModel> All =
    [
        new(
            "piper-libritts-r-fp32",
            "Piper LibriTTS-R medium fp32",
            TtsRuntime.Vits,
            "vits-piper-en_US-libritts_r-medium.tar.bz2",
            PiperLicense,
            PiperVoices
        ),
        new(
            "piper-libritts-r-fp16",
            "Piper LibriTTS-R medium fp16",
            TtsRuntime.Vits,
            "vits-piper-en_US-libritts_r-medium-fp16.tar.bz2",
            PiperLicense,
            PiperVoices
        ),
        new(
            "piper-libritts-r-int8",
            "Piper LibriTTS-R medium int8",
            TtsRuntime.Vits,
            "vits-piper-en_US-libritts_r-medium-int8.tar.bz2",
            PiperLicense,
            PiperVoices
        ),
        new("kitten-nano-0.8", "Kitten nano 0.8 fp32", TtsRuntime.Kitten, "kitten-nano-en-v0_8-fp32.tar.bz2", KittenLicense, KittenVoices),
        new("kitten-micro-0.8", "Kitten micro 0.8", TtsRuntime.Kitten, "kitten-micro-en-v0_8.tar.bz2", KittenLicense, KittenVoices),
        new("kitten-mini-0.8", "Kitten mini 0.8", TtsRuntime.Kitten, "kitten-mini-en-v0_8.tar.bz2", KittenLicense, KittenVoices),
        new(
            "matcha-ljspeech",
            "Matcha (icefall LJSpeech) + Vocos 22 kHz",
            TtsRuntime.Matcha,
            "matcha-icefall-en_US-ljspeech.tar.bz2",
            "No licence file in the archive (LJSpeech is public domain)",
            [new("ljspeech", 0, null)]
        ),
        new(
            "pocket",
            "Pocket TTS fp32",
            TtsRuntime.Pocket,
            "sherpa-onnx-pocket-tts-2026-01-26.tar.bz2",
            "CC BY 4.0 (LICENSE in the archive)",
            CloneVoices
        ),
        new(
            "pocket-int8",
            "Pocket TTS int8",
            TtsRuntime.Pocket,
            "sherpa-onnx-pocket-tts-int8-2026-01-26.tar.bz2",
            "CC BY 4.0 (LICENSE in the archive)",
            CloneVoices
        ),
        new(
            "zipvoice-int8",
            "ZipVoice distill int8 (zh-en Emilia) + Vocos 24 kHz",
            TtsRuntime.ZipVoice,
            "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia.tar.bz2",
            "No licence file in the archive (trained on Emilia, CC BY-NC 4.0)",
            CloneVoices
        ),
        new(
            "supertonic-3-int8",
            "Supertonic 3 int8",
            TtsRuntime.Supertonic,
            "sherpa-onnx-supertonic-3-tts-int8-2026-05-11.tar.bz2",
            "MIT (LICENSE in the archive)",
            [new("sid-0", 0, null), new("sid-1", 1, null), new("sid-2", 2, null)]
        ),
        new(
            "kitten-tts-2",
            "Kitten-TTS 2 (kitten_tts_cpp, student_w4 decoder)",
            TtsRuntime.KittenTts2,
            null,
            "Stellon Labs Community License",
            [new("Bruno", -1, null), new("Jasper", -1, null), new("Hugo", -1, null)]
        ),
    ];

    /// <summary>The vocoder file a model needs beside its archive, or null.</summary>
    public static string? VocoderFor(TtsBenchModel model) =>
        model.Runtime switch
        {
            TtsRuntime.Matcha => Vocos22k,
            TtsRuntime.ZipVoice => Vocos24k,
            _ => null,
        };

    /// <summary>Whether the runtime renders text from espeak-ng phonemes, so the caller phonemizes first.</summary>
    public static bool NeedsPhonemes(TtsRuntime runtime) => runtime is TtsRuntime.Vits or TtsRuntime.Kitten or TtsRuntime.Matcha;

    /// <summary>The <c>espeak-ng-data</c> directory a model archive carries beside its tokens.</summary>
    public static string EspeakDataDir(string modelDir) => Path.Combine(modelDir, "espeak-ng-data");

    /// <summary>Builds the CPU config at <paramref name="numThreads"/> for a sherpa-onnx model extracted at <paramref name="dir"/>.</summary>
    public static OfflineTtsConfig BuildConfig(TtsBenchModel model, string dir, string? vocoderPath, int numThreads)
    {
        var config = new OfflineTtsConfig();
        config.Model.NumThreads = numThreads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        string tokens = Path.Combine(dir, "tokens.txt");
        string espeak = EspeakDataDir(dir);
        switch (model.Runtime)
        {
            case TtsRuntime.Vits:
                config.Model.Vits.Model = One(dir, "*.onnx");
                config.Model.Vits.Tokens = tokens;
                config.Model.Vits.LengthScale = 1.0f;
                break;
            case TtsRuntime.Kitten:
                config.Model.Kitten.Model = One(dir, "model*.onnx");
                config.Model.Kitten.Voices = Path.Combine(dir, "voices.bin");
                config.Model.Kitten.Tokens = tokens;
                break;
            case TtsRuntime.Matcha:
                config.Model.Matcha.AcousticModel = One(dir, "model-steps-*.onnx");
                config.Model.Matcha.Vocoder = vocoderPath ?? throw new InvalidOperationException("Matcha needs its vocoder path.");
                config.Model.Matcha.Tokens = tokens;
                break;
            case TtsRuntime.Pocket:
                BindPocket(config.Model.Pocket, dir);
                break;
            case TtsRuntime.ZipVoice:
                config.Model.ZipVoice.Tokens = tokens;
                config.Model.ZipVoice.Encoder = One(dir, "encoder*.onnx");
                config.Model.ZipVoice.Decoder = One(dir, "decoder*.onnx");
                config.Model.ZipVoice.Vocoder = vocoderPath ?? throw new InvalidOperationException("ZipVoice needs its vocoder path.");
                config.Model.ZipVoice.DataDir = espeak;
                config.Model.ZipVoice.Lexicon = Path.Combine(dir, "lexicon.txt");
                break;
            case TtsRuntime.Supertonic:
                BindSupertonic(config.Model.Supertonic, dir);
                break;
            default:
                throw new InvalidOperationException($"{model.Id} does not run on sherpa-onnx.");
        }
        return config;
    }

    private static void BindPocket(OfflineTtsPocketModelConfig pocket, string dir)
    {
        pocket.LmFlow = One(dir, "lm_flow*.onnx");
        pocket.LmMain = One(dir, "lm_main*.onnx");
        pocket.Encoder = One(dir, "encoder*.onnx");
        pocket.Decoder = One(dir, "decoder*.onnx");
        pocket.TextConditioner = One(dir, "text_conditioner*.onnx");
        pocket.VocabJson = Path.Combine(dir, "vocab.json");
        pocket.TokenScoresJson = Path.Combine(dir, "token_scores.json");
    }

    private static void BindSupertonic(OfflineTtsSupertonicModelConfig supertonic, string dir)
    {
        supertonic.DurationPredictor = One(dir, "duration_predictor*.onnx");
        supertonic.TextEncoder = One(dir, "text_encoder*.onnx");
        supertonic.VectorEstimator = One(dir, "vector_estimator*.onnx");
        supertonic.Vocoder = One(dir, "vocoder*.onnx");
        supertonic.TtsJson = Path.Combine(dir, "tts.json");
        supertonic.UnicodeIndexer = Path.Combine(dir, "unicode_indexer.bin");
        supertonic.VoiceStyle = Path.Combine(dir, "voice.bin");
    }

    /// <summary>The one file in <paramref name="dir"/> matching <paramref name="pattern"/>; fails when there is none or more than one.</summary>
    private static string One(string dir, string pattern)
    {
        string[] matches = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
        return matches.Length == 1
            ? matches[0]
            : throw new FileNotFoundException($"Expected exactly one '{pattern}' in {dir}, found {matches.Length}.");
    }
}
