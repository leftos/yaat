using System.ComponentModel;
using System.Net.Http;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Settings > Speech's model, mode and device: what is available and why, what Apply writes, and what Reset restores. Each
/// test reads the n-best check from its own native folder, so the test's folder, not the app's, decides Accurate.
/// </summary>
public sealed class SettingsViewModelSttTests : IDisposable
{
    private const string NbestReason = "Accurate needs the n-best native; running Fast";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-settings-stt-tests", Guid.NewGuid().ToString("N"));
    private readonly List<SttDownloads> _downloads = [];
    private readonly List<SettingsViewModel> _viewModels = [];
    private readonly (SttEngine Engine, SttMode Mode, SttDevice Device) _savedStt;

    public SettingsViewModelSttTests()
    {
        var preferences = new UserPreferences();
        _savedStt = (preferences.SttEngine, preferences.SttMode, preferences.SttDevice);
    }

    public void Dispose()
    {
        foreach (SettingsViewModel vm in _viewModels)
        {
            vm.ReleaseSttDownloads();
        }
        foreach (SttDownloads downloads in _downloads)
        {
            downloads.Dispose();
        }
        SaveStt(_savedStt.Engine, _savedStt.Mode, _savedStt.Device);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void WhisperSelected_DisablesAccurateWithTheWhisperReason_KeepsAccurate_AndShowsTheSizePicker()
    {
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu);
        SettingsViewModel vm = NewViewModel(nbestAvailable: true);

        vm.SelectedSttModelCard = vm.SttModelCards.Single(c => c.IsWhisper);

        Assert.False(vm.IsAccurateAvailable);
        Assert.Equal(SherpaNativeLoader.WhisperAccurateReason, vm.AccurateUnavailableReason);
        Assert.Equal(SttMode.Accurate, vm.SelectedSttMode);
        Assert.True(vm.IsWhisperSizePickerVisible);
        vm.SelectedSttModelCard = vm.SttModelCards.First(c => !c.IsWhisper);
        Assert.False(vm.IsWhisperSizePickerVisible);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task NbestUnavailable_DisablesAccurateWithTheReason_AndKeepsAccurateSelected()
    {
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu);
        SettingsViewModel vm = NewViewModel(nbestAvailable: false);

        await UntilAsync(vm, () => !vm.IsAccurateAvailable);

        Assert.Equal(NbestReason, vm.AccurateUnavailableReason);
        Assert.Equal(SttMode.Accurate, vm.SelectedSttMode);
        Assert.False(vm.SelectedSttModelCard!.IsWhisper);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task GpuSavedButUnavailable_SaysRunningOnCpu_AndKeepsGpuSelected()
    {
        // The test's app data folder holds no GPU runtime, so a GPU pick cannot run whatever the machine's card is.
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Gpu);
        SettingsViewModel vm = NewViewModel(nbestAvailable: true);
        Assert.True(vm.IsGpuChecking);
        Assert.Equal(CudaDriverProbe.CheckingText, vm.DeviceStatusText);

        await UntilAsync(vm, () => !vm.IsGpuChecking);

        Assert.False(vm.IsGpuAvailable);
        Assert.EndsWith("Running on CPU", vm.DeviceStatusText, StringComparison.Ordinal);
        Assert.Equal(SttDevice.Gpu, vm.SelectedSttDevice);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_WritesTheThreeSettings_AndFlagsOnlyAnEngineOrModeChange()
    {
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu);
        SettingsViewModel vm = NewViewModel(nbestAvailable: true);

        vm.SelectedSttDevice = SttDevice.Gpu;
        vm.ApplyCommand.Execute(null);
        Assert.False(vm.LastApplyChangedSttEngineOrMode);
        Assert.Equal(SttDevice.Gpu, new UserPreferences().SttDevice);

        vm.SelectedSttMode = SttMode.Fast;
        vm.ApplyCommand.Execute(null);
        Assert.True(vm.LastApplyChangedSttEngineOrMode);
        Assert.Equal(SttMode.Fast, new UserPreferences().SttMode);

        vm.ApplyCommand.Execute(null);
        Assert.False(vm.LastApplyChangedSttEngineOrMode);

        vm.SelectedSttModelCard = vm.SttModelCards.Single(c => c.Engine == SttEngine.RasrV2Int8);
        vm.ApplyCommand.Execute(null);
        Assert.True(vm.LastApplyChangedSttEngineOrMode);
        var stored = new UserPreferences();
        Assert.Equal((SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Gpu), (stored.SttEngine, stored.SttMode, stored.SttDevice));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ResetSpeech_RestoresTheDefaultEngineModeAndDevice()
    {
        SaveStt(SttEngine.Whisper, SttMode.Fast, SttDevice.Gpu);
        SettingsViewModel vm = NewViewModel(nbestAvailable: true);
        Assert.True(vm.SelectedSttModelCard!.IsWhisper);

        vm.SelectedSection = SettingsSectionId.Speech;
        vm.ResetSectionCommand.Execute(null);

        var defaults = UserPreferences.CreateDefaults();
        Assert.Equal(
            (defaults.SttEngine, defaults.SttMode, defaults.SttDevice),
            (vm.SelectedSttModelCard!.Engine, vm.SelectedSttMode, vm.SelectedSttDevice)
        );
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task EngineLoading_MarksOnlyTheSelectedCard()
    {
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu);
        SttDownloads downloads = NewDownloads(SttDownloads.DefaultManifestPath, new HttpClient(), nbestAvailable: true);
        SettingsViewModel vm = NewViewModel(downloads);
        SttModelCardViewModel selected = vm.SelectedSttModelCard!;
        var load = new TaskCompletionSource();

        Task tracked = downloads.TrackEngineLoadAsync(() => load.Task);
        await UntilAsync(selected, () => selected.IsLoading);

        Assert.All(vm.SttModelCards.Where(c => !ReferenceEquals(c, selected)), c => Assert.False(c.IsLoading));
        load.SetResult();
        await tracked;
        await UntilAsync(selected, () => !selected.IsLoading);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task ReopenedSettings_ShowsARunningDownload_AndItsCancelStopsIt()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SttDownloads downloads = NewDownloads(WriteManifest(), new HttpClient(new HeldHandler(requested)), nbestAvailable: true);
        SettingsViewModel first = NewViewModel(downloads);
        first.SttModelCards.Single(c => c.Engine == SttEngine.RasrV2Fp32).DownloadCommand.Execute(null);
        await requested.Task;
        first.ReleaseSttDownloads();

        SettingsViewModel reopened = NewViewModel(downloads);
        SttModelCardViewModel card = reopened.SttModelCards.Single(c => c.Engine == SttEngine.RasrV2Fp32);
        Assert.True(card.Install!.IsBusy);
        Assert.EndsWith(" of 0.00 GB", card.StateText, StringComparison.Ordinal);
        Assert.True(card.CancelDownloadCommand.CanExecute(null));

        card.CancelDownloadCommand.Execute(null);
        await UntilAsync(card.Install, () => !card.Install.IsBusy);

        Assert.Equal("Cancelled.", card.Install.StatusMessage);
        Assert.False(card.Install.IsInstalled);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ReleaseSttDownloads_DisposesOnlyAnOwnedSttDownloads()
    {
        SttDownloads shared = NewDownloads(SttDownloads.DefaultManifestPath, new HttpClient(), nbestAvailable: true);
        SettingsViewModel borrowing = NewViewModel(shared);
        var owning = new SettingsViewModel(new UserPreferences());

        borrowing.ReleaseSttDownloads();
        owning.ReleaseSttDownloads();

        Assert.False(shared.IsDisposed);
        Assert.True(owning.SttDownloads.IsDisposed);
    }

    private SettingsViewModel NewViewModel(bool nbestAvailable) =>
        NewViewModel(NewDownloads(SttDownloads.DefaultManifestPath, new HttpClient(), nbestAvailable));

    private SettingsViewModel NewViewModel(SttDownloads downloads)
    {
        var vm = new SettingsViewModel(new UserPreferences(), audioCapture: null, speechSampleStore: null, downloads);
        _viewModels.Add(vm);
        return vm;
    }

    private SttDownloads NewDownloads(string manifestPath, HttpClient http, bool nbestAvailable)
    {
        string nativeDir = Path.Combine(_root, nbestAvailable ? "native" : "stock-native");
        Directory.CreateDirectory(nativeDir);
        if (!nbestAvailable)
        {
            File.WriteAllText(Path.Combine(nativeDir, "sherpa-native-FALLBACK.txt"), "stock native");
        }
        var downloads = new SttDownloads(manifestPath, Path.Combine(_root, "models"), TimeProvider.System, http, nativeDir);
        _downloads.Add(downloads);
        return downloads;
    }

    /// <summary>Writes a manifest whose fp32 model is one small file, served by <see cref="HeldHandler"/>.</summary>
    private string WriteManifest()
    {
        byte[] body = "a b"u8.ToArray();
        var manifest = new
        {
            baseUrl = "https://r2.example/",
            models = new[]
            {
                new
                {
                    id = "rasr-parakeet-v2-fp32",
                    files = new[]
                    {
                        new
                        {
                            name = "tokens.txt",
                            key = "stt/tokens.txt",
                            size = body.Length,
                            sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)),
                        },
                    },
                },
            },
        };
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(manifest));
        return path;
    }

    /// <summary>Signals the request and holds it until the download is cancelled.</summary>
    private sealed class HeldHandler(TaskCompletionSource requested) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A held request ended without being cancelled.");
        }
    }

    private static void SaveStt(SttEngine engine, SttMode mode, SttDevice device)
    {
        var preferences = new UserPreferences();
        preferences.SetSpeechSettings(
            preferences.SpeechEnabled,
            preferences.WhisperModelSize,
            preferences.LlmModelPath,
            preferences.LlmGpuLayers,
            preferences.AutoFocusInputAfterSpeech,
            engine,
            mode,
            device
        );
    }

    /// <summary>Completes once <paramref name="done"/> holds, checked now and on each property change of <paramref name="vm"/>.</summary>
    private static async Task UntilAsync(INotifyPropertyChanged vm, Func<bool> done)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (done())
            {
                reached.TrySetResult();
            }
        }
        vm.PropertyChanged += OnChanged;
        try
        {
            if (!done())
            {
                await reached.Task;
            }
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }
    }
}
