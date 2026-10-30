using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>Whisper config carrying an explicit model source (the prefs default or a <c>--whisper</c> override).</summary>
internal sealed class FixedWhisperRuntimeConfig(string modelSource) : IWhisperRuntimeConfig
{
    public string ModelSource { get; } = modelSource;
}
