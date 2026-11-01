namespace Yaat.Client.Models;

/// <summary>Which speech-recognition model transcribes the user's transmissions.</summary>
public enum SttEngine
{
    /// <summary>The rasr Parakeet v2 model at full (fp32) precision. The default.</summary>
    RasrV2Fp32,

    /// <summary>The rasr Parakeet v2 model quantised to int8: smaller and faster, slightly less accurate.</summary>
    RasrV2Int8,

    /// <summary>The legacy Whisper model, sized by the Whisper model preference.</summary>
    Whisper,
}

/// <summary>Lookups over <see cref="SttEngine"/>.</summary>
public static class SttEngines
{
    /// <summary>The model id the download manifest lists for an engine.</summary>
    /// <param name="engine">The engine to look up.</param>
    /// <returns>The manifest model id, or null for <see cref="SttEngine.Whisper"/>, which the manifest does not list.</returns>
    public static string? ManifestModelId(this SttEngine engine) =>
        engine switch
        {
            SttEngine.RasrV2Fp32 => "rasr-parakeet-v2-fp32",
            SttEngine.RasrV2Int8 => "rasr-parakeet-v2-int8",
            SttEngine.Whisper => null,
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown speech-recognition engine"),
        };
}
