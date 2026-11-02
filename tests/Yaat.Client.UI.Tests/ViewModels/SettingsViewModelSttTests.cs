using System.ComponentModel;
using System.Net.Http;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;
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
    public async Task CpuFallbackReasonChanged_RefreshesTheDeviceStatus_UntilTheWindowReleases()
    {
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu);
        string? savedReason = SherpaNativeLoader.CpuFallbackReason;
        SherpaNativeLoader.ResetCpuFallbackForTests(null);
        try
        {
            SettingsViewModel vm = NewViewModel(nbestAvailable: true);
            await UntilAsync(vm, () => !vm.IsGpuChecking);
            // Empty only when the card can run GPU speech; a card that cannot names its own reason, which wins.
            string before = vm.DeviceStatusText;
            List<string?> changed = [];
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            SherpaNativeLoader.RecordCpuFallback(SherpaSttEngine.NoGpuMemoryRiseReason);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(nameof(SettingsViewModel.DeviceStatusText), changed);
            Assert.Equal((before.Length == 0) ? SherpaSttEngine.NoGpuMemoryRiseReason : before, vm.DeviceStatusText);

            vm.ReleaseSttDownloads();
            changed.Clear();
            SherpaNativeLoader.RecordGpuLoad();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(nameof(SettingsViewModel.DeviceStatusText), changed);
        }
        finally
        {
            SherpaNativeLoader.ResetCpuFallbackForTests(savedReason);
        }
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

    [AvaloniaFact(Timeout = 60_000)]
    public void BenchmarkRow_IsHiddenWithoutAService()
    {
        SettingsViewModel vm = NewViewModel(nbestAvailable: true);

        Assert.False(vm.IsBenchmarkAvailable);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task BenchmarkSummary_FollowsTheServiceThroughARun()
    {
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu);
        var fakes = new BenchFakes { Plan = [new SttBenchPlanRow(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, null)] };
        SttBenchService bench = NewBench(fakes);
        SettingsViewModel vm = NewViewModel(BenchDownloads(), bench);
        Assert.True(vm.IsBenchmarkAvailable);
        Assert.Equal(("Never run on this machine", "", ""), Summary(vm));
        // Counted rather than asserted in the handler: the service logs and drops whatever a listener throws.
        int changesOffTheUiThread = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if ((e.PropertyName == nameof(SettingsViewModel.BenchmarkSummaryText)) && (!Dispatcher.UIThread.CheckAccess()))
            {
                Interlocked.Increment(ref changesOffTheUiThread);
            }
        };

        fakes.Runner.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fakes.Runner.RowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = bench.RunAsync(CancellationToken.None);
        await fakes.Runner.RowStarted.Task;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("Running 1 of 1…", "", ""), Summary(vm));

        fakes.Runner.Hold.TrySetResult();
        await run;
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("Last run ", vm.BenchmarkSummaryText, StringComparison.Ordinal);
        Assert.EndsWith(": 1 run", vm.BenchmarkSummaryText, StringComparison.Ordinal);
        Assert.Equal("", vm.BenchmarkFailedText);
        Assert.Equal("· this setting 410 ms p50, 1.1 GB RAM", vm.BenchmarkSummaryTail);
        Assert.Equal("410 ms p50, 1.1 GB RAM (Fast, CPU)", vm.SelectedSttModelCard!.MeasuredText);
        Assert.Equal(0, changesOffTheUiThread);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CardMeasured_FollowsTheSelectedModeAndDevice()
    {
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu);
        SttBenchLastRunStore.Save(
            LastRunPath,
            new SttBenchLastRun(
                DateTimeOffset.UnixEpoch,
                [
                    new SttBenchLastRow(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, BenchResult(SttDevice.Cpu, SttMode.Fast), null),
                    new SttBenchLastRow(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu, BenchResult(SttDevice.Gpu, SttMode.Accurate), null),
                ]
            )
        );
        SettingsViewModel vm = NewViewModel(BenchDownloads(), NewBench(new BenchFakes()));
        SttModelCardViewModel fp32 = vm.SttModelCards.Single(c => c.Engine == SttEngine.RasrV2Fp32);
        SttModelCardViewModel int8 = vm.SttModelCards.Single(c => c.Engine == SttEngine.RasrV2Int8);
        Assert.Equal("410 ms p50, 1.1 GB RAM (Fast, CPU)", fp32.MeasuredText);
        Assert.Equal(SttModelCardViewModel.NotMeasuredText, int8.MeasuredText);

        vm.SelectedSttMode = SttMode.Accurate;
        Assert.Equal(SttModelCardViewModel.NotMeasuredText, fp32.MeasuredText);

        vm.SelectedSttDevice = SttDevice.Gpu;
        Assert.Equal("410 ms p50, 3.6 GB VRAM (Accurate, GPU)", fp32.MeasuredText);

        vm.SelectedSttDevice = SttDevice.Cpu;
        Assert.Equal(SttModelCardViewModel.NotMeasuredText, fp32.MeasuredText);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ShowSavedSttChoice_SetsTheSelectionWithoutSaving()
    {
        SaveStt(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu);
        var preferences = new UserPreferences();
        var vm = new SettingsViewModel(preferences, audioCapture: null, speechSampleStore: null, BenchDownloads(), sttBench: null);
        _viewModels.Add(vm);

        vm.ShowSavedSttChoice(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Gpu);

        Assert.Equal(
            (SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Gpu),
            (vm.SelectedSttModelCard!.Engine, vm.SelectedSttMode, vm.SelectedSttDevice)
        );
        var stored = new UserPreferences();
        Assert.Equal((SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu), (stored.SttEngine, stored.SttMode, stored.SttDevice));

        // The benchmark window's Apply has saved the choice before it shows here, so Settings' own Apply writes the same values.
        SaveStt(preferences, SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Gpu);
        vm.ApplyCommand.Execute(null);

        Assert.False(vm.LastApplyChangedSttEngineOrMode);
        stored = new UserPreferences();
        Assert.Equal((SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Gpu), (stored.SttEngine, stored.SttMode, stored.SttDevice));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Release_UnsubscribesFromTheService()
    {
        var fakes = new BenchFakes { Plan = [new SttBenchPlanRow(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, null)] };
        SttBenchService bench = NewBench(fakes);
        SettingsViewModel vm = NewViewModel(BenchDownloads(), bench);
        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ReleaseSttDownloads();
        await bench.RunAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(nameof(SettingsViewModel.BenchmarkSummaryText), changed);
        Assert.Equal("Never run on this machine", vm.BenchmarkSummaryText);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task APostQueuedBeforeRelease_ChangesNothing()
    {
        var fakes = new BenchFakes { Plan = [new SttBenchPlanRow(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, null)] };
        SttBenchService bench = NewBench(fakes);
        SettingsViewModel vm = NewViewModel(BenchDownloads(), bench);
        List<string?> changed = [];
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // The service raises its first change before it first awaits, so the post is queued and nothing has drained it yet.
        Task run = bench.RunAsync(CancellationToken.None);
        vm.ReleaseSttDownloads();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(nameof(SettingsViewModel.BenchmarkSummaryText), changed);
        Assert.Equal("Never run on this machine", vm.BenchmarkSummaryText);
        await run;
    }

    private string LastRunPath => Path.Combine(_root, "bench", "last-run.json");

    private static SttBenchResult BenchResult(SttDevice device, SttMode mode) => SttBenchResult.Measured(BenchFakes.Timing(device), 12, mode);

    private static (string, string, string) Summary(SettingsViewModel vm) =>
        (vm.BenchmarkSummaryText, vm.BenchmarkFailedText, vm.BenchmarkSummaryTail);

    private SttDownloads BenchDownloads() => NewDownloads(SttDownloads.DefaultManifestPath, new HttpClient(), nbestAvailable: true);

    private SttBenchService NewBench(BenchFakes fakes)
    {
        string bench = Path.Combine(_root, "bench");
        var folders = new SttBenchFolders(Path.Combine(bench, "models"), Path.Combine(bench, "clips"), Path.Combine(bench, "runs"), LastRunPath);
        return new SttBenchService(fakes.PlanAsync, fakes.Runner, fakes.Suspender, folders, new FakeTimeProvider());
    }

    private SettingsViewModel NewViewModel(bool nbestAvailable) =>
        NewViewModel(NewDownloads(SttDownloads.DefaultManifestPath, new HttpClient(), nbestAvailable));

    private SettingsViewModel NewViewModel(SttDownloads downloads) => NewViewModel(downloads, sttBench: null);

    private SettingsViewModel NewViewModel(SttDownloads downloads, SttBenchService? sttBench)
    {
        var vm = new SettingsViewModel(new UserPreferences(), audioCapture: null, speechSampleStore: null, downloads, sttBench);
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

    private static void SaveStt(SttEngine engine, SttMode mode, SttDevice device) => SaveStt(new UserPreferences(), engine, mode, device);

    private static void SaveStt(UserPreferences preferences, SttEngine engine, SttMode mode, SttDevice device)
    {
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
