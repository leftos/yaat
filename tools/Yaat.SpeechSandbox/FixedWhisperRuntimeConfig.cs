using Yaat.Client.Services;

namespace Yaat.SpeechSandbox;

/// <summary>
/// Whisper config carrying an explicit model source (the prefs default or a <c>--stt whisper:</c> source)
/// and GPU layer count: 0 for a <c>--device cpu</c> run, null for LM-Kit's own choice.
/// </summary>
internal sealed class FixedWhisperRuntimeConfig(string modelSource, int? gpuLayerCount) : IWhisperRuntimeConfig
{
    public string ModelSource { get; } = modelSource;

    public int? GpuLayerCount { get; } = gpuLayerCount;
}
