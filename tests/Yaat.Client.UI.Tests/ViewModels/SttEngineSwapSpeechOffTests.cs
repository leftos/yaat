using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>With speech off, a swap builds the engine without loading its model: nothing that swaps may raise the client's memory.</summary>
public sealed class SttEngineSwapSpeechOffTests
{
    [AvaloniaFact(Timeout = 300_000)]
    public async Task SwapSttEngine_WithSpeechOff_DoesNotOpenTheModel()
    {
        string modelDir = SttModelInstaller.ModelDir(SttModelInstaller.InstallRootDefault, "rasr-parakeet-v2-fp32");
        Directory.CreateDirectory(modelDir);
        foreach (string file in (string[])["encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt", "bpe.vocab"])
        {
            File.WriteAllText(Path.Combine(modelDir, file), "not a model: opening this fails");
        }
        var previous = new UserPreferences();
        (bool speech, string whisper, string llm, int layers, bool focus, SttEngine engine, SttMode mode, SttDevice device) = (
            previous.SpeechEnabled,
            previous.WhisperModelSize,
            previous.LlmModelPath,
            previous.LlmGpuLayers,
            previous.AutoFocusInputAfterSpeech,
            previous.SttEngine,
            previous.SttMode,
            previous.SttDevice
        );
        try
        {
            previous.SetSpeechSettings(false, "", "", layers, focus, SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu);
            var vm = new MainViewModel(new FakeFilePickerService());

            Exception? thrown = await Record.ExceptionAsync(vm.SwapSttEngineAsync);

            Assert.Null(thrown);
        }
        finally
        {
            new UserPreferences().SetSpeechSettings(speech, whisper, llm, layers, focus, engine, mode, device);
            Directory.Delete(modelDir, recursive: true);
        }
    }
}
