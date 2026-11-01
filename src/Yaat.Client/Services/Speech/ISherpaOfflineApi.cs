using SherpaOnnx;

namespace Yaat.Client.Services.Speech;

/// <summary>The sherpa-onnx C API calls one offline transducer engine makes, as a seam a test can stand in for.</summary>
public interface ISherpaOfflineApi
{
    /// <summary>Builds a recognizer from the config; zero when the native rejects it.</summary>
    IntPtr CreateRecognizer(ref OfflineRecognizerConfig config);

    /// <summary>Frees a recognizer.</summary>
    void DestroyRecognizer(IntPtr recognizer);

    /// <summary>
    /// Creates a stream on the recognizer, biased by <paramref name="hotwords"/> when it is non-empty; zero when the native
    /// declines.
    /// </summary>
    IntPtr CreateStream(IntPtr recognizer, string hotwords);

    /// <summary>Sets a per-stream runtime option.</summary>
    void SetStreamOption(IntPtr stream, string key, string value);

    /// <summary>Hands the utterance to the stream.</summary>
    void AcceptWaveform(IntPtr stream, int sampleRate, float[] samples);

    /// <summary>Decodes the stream on the recognizer.</summary>
    void Decode(IntPtr recognizer, IntPtr stream);

    /// <summary>The stream's result as JSON; null when the native wrote none.</summary>
    string? ResultJson(IntPtr stream);

    /// <summary>Frees a stream.</summary>
    void DestroyStream(IntPtr stream);
}
