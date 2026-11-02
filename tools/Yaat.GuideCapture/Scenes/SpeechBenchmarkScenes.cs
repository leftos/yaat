using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Customization > Speech benchmark, and the release showcase. The window after a finished run, one row picked:
// a real service reads a seeded last run from a temp folder, and no benchmark runs, so no child process or speech model is needed.
internal abstract class SpeechBenchmarkSceneBase : StandaloneWindowSceneBase
{
    private const int ClipCount = 12;
    private const long NotMeasured = -1;

    private static readonly SttEngine[] Models = [SttEngine.RasrV2Fp32, SttEngine.RasrV2Int8];

    private static readonly SpeechBenchmarkRowKey PickedRow = new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Gpu);

    private static readonly SeededRow[] SeededRows =
    [
        new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu, 6.2, 401, 679, 3_000_000_000, NotMeasured),
        new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Cpu, 6.0, 277, 472, 3_000_000_000, NotMeasured),
        new(SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Gpu, 9.5, 450, 742, 1_400_000_000, 3_600_000_000),
        new(SttEngine.RasrV2Fp32, SttMode.Fast, SttDevice.Gpu, 9.3, 112, 141, 1_400_000_000, 3_600_000_000),
        new(SttEngine.RasrV2Int8, SttMode.Accurate, SttDevice.Cpu, 3.1, 323, 531, 1_300_000_000, NotMeasured),
        new(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Cpu, 3.0, 238, 394, 1_300_000_000, NotMeasured),
        new(SttEngine.RasrV2Int8, SttMode.Accurate, SttDevice.Gpu, 5.4, 729, 1166, 1_800_000_000, 1_000_000_000),
        new(SttEngine.RasrV2Int8, SttMode.Fast, SttDevice.Gpu, 5.3, 370, 595, 1_800_000_000, 1_000_000_000),
    ];

    private DirectoryInfo? _tempFolder;

    public override Window CreateWindow(CaptureContext ctx)
    {
        _tempFolder = Directory.CreateTempSubdirectory("yaat-guidecapture-speech-benchmark-");
        string root = _tempFolder.FullName;
        var folders = new SttBenchFolders(
            Path.Combine(root, "models"),
            Path.Combine(root, "clips"),
            Path.Combine(root, "runs"),
            Path.Combine(root, "stt-benchmark.json")
        );
        DateTimeOffset finished = FixedTimeProvider.CaptureInstant.GetUtcNow();
        SttBenchLastRunStore.Save(folders.LastRunPath, new SttBenchLastRun(finished, [.. SeededRows.Select(row => row.ToLastRow())]));

        var service = new SttBenchService(Plan, new NoRowRunner(), new NoEngineSuspender(), folders, FixedTimeProvider.CaptureInstant);
        var viewModel = new SpeechBenchmarkViewModel(
            service,
            Plan,
            _ => true,
            SttReference.Load(Path.Combine(ctx.RepoRoot, "src", "Yaat.Client", "Speech", "stt-reference.json")),
            () => (SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu),
            (_, _, _) => Task.CompletedTask,
            () => SttDevice.Cpu,
            TimeZoneInfo.Utc
        );
        return new SpeechBenchmarkWindow(viewModel, new UserPreferences());
    }

    public override Task AfterShowAsync(Window window, CaptureContext ctx)
    {
        var benchmark = (SpeechBenchmarkWindow)window;
        benchmark.ViewModel.Rows.Single(row => row.Key == PickedRow).IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        benchmark.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return Task.CompletedTask;
    }

    public override void AfterCapture()
    {
        _tempFolder?.Delete(recursive: true);
        _tempFolder = null;
    }

    private static Task<IReadOnlyList<SttBenchPlanRow>> Plan() => Task.FromResult(SttBenchPlan.Build(Models, null, null));

    private sealed record SeededRow(
        SttEngine Engine,
        SttMode Mode,
        SttDevice Device,
        double LoadSeconds,
        double LatencyP50Ms,
        double LatencyP90Ms,
        long PeakWorkingSetBytes,
        long PeakGpuBytes
    )
    {
        public SttBenchLastRow ToLastRow()
        {
            string device = (Device == SttDevice.Gpu) ? "auto" : "cpu";
            var timing = new CorpusWerTiming(
                device,
                LoadSeconds,
                ClipCount,
                0,
                LatencyP50Ms,
                LatencyP90Ms,
                LatencyP90Ms,
                0,
                PeakWorkingSetBytes,
                NotMeasured,
                PeakGpuBytes,
                null
            );
            return new SttBenchLastRow(Engine, Mode, Device, SttBenchResult.Measured(timing, ClipCount, Mode), null);
        }
    }

    private sealed class NoRowRunner : ISttBenchRowRunner
    {
        public Task<SttBenchRowOutcome> RunRowAsync(SttBenchJob job, TimeSpan deadline, CancellationToken ct) =>
            throw new InvalidOperationException("A capture scene never runs a benchmark row");
    }

    private sealed class NoEngineSuspender : ISttEngineSuspender
    {
        public Task SuspendAsync(CancellationToken ct) => throw new InvalidOperationException("A capture scene never suspends the speech engine");

        public Task ResumeAsync(CancellationToken ct) => throw new InvalidOperationException("A capture scene never resumes the speech engine");
    }
}

internal sealed class SpeechBenchmarkScene : SpeechBenchmarkSceneBase
{
    public override string Name => "speech-benchmark";
}

internal sealed class WhatsNewSpeechBenchmarkScene : SpeechBenchmarkSceneBase
{
    public override string Name => "whats-new-speech-benchmark";
}
