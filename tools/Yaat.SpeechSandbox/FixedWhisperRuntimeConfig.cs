using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>Whisper config carrying an explicit model source (the prefs default or a <c>--stt whisper:</c> source).</summary>
internal sealed class FixedWhisperRuntimeConfig(string modelSource) : IWhisperRuntimeConfig
{
    public string ModelSource { get; } = modelSource;
}
