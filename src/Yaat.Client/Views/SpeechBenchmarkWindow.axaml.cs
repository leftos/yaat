using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech.Bench;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// The "Speech benchmark" window: Run/Cancel, the table of this machine's figures beside the reference ones, Apply and Close.
/// Closing while a run goes on swaps the footer for an ask bar (cancel the run, or keep it running and close); while the
/// speech models unload or reload the window refuses to close.
/// </summary>
public partial class SpeechBenchmarkWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<SpeechBenchmarkWindow>();

    private readonly SpeechBenchmarkViewModel _viewModel;
    private bool _closeAllowed;

    // Parameterless ctor for the Avalonia designer / XamlLoader: a window over a benchmark that never runs.
    public SpeechBenchmarkWindow()
        : this(DesignerViewModel(), new UserPreferences()) { }

    public SpeechBenchmarkWindow(SpeechBenchmarkViewModel viewModel, UserPreferences preferences)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "SpeechBenchmark", 900, 520).Restore();

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("CancelRunButton")!.Click += (_, _) => _viewModel.CancelAndClose();
        this.FindControl<Button>("KeepRunningButton")!.Click += OnKeepRunningClick;
        viewModel.CloseRequested += OnCloseRequested;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>The benchmark this window shows; also its DataContext.</summary>
    public SpeechBenchmarkViewModel ViewModel => _viewModel;

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await _viewModel.LoadPlanAsync();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Opening the speech benchmark window failed");
        }
    }

    // Every close goes through the view model; app and OS shutdown and the owner's close are never held up by a run. A refused
    // close returns before base.OnClosing, so no Closing handler (the geometry helper's among them) sees a close that never
    // happens.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        bool proceeds =
            _closeAllowed
            || (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown or WindowCloseReason.OwnerWindowClosing)
            || (_viewModel.RequestClose() == CloseDecision.Close);
        if (!proceeds)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void OnKeepRunningClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.KeepRunning();
        CloseAllowed();
    }

    private void OnCloseRequested() => CloseAllowed();

    private void CloseAllowed()
    {
        _closeAllowed = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
    }

    private static SpeechBenchmarkViewModel DesignerViewModel()
    {
        var bench = new DesignerBench();
        string none = Path.Combine(Path.GetTempPath(), "yaat-speech-benchmark-designer");
        var folders = new SttBenchFolders(none, none, none, Path.Combine(none, "last-run.json"));
        static Task<IReadOnlyList<SttBenchPlanRow>> Plan() => Task.FromResult<IReadOnlyList<SttBenchPlanRow>>([]);
        return new SpeechBenchmarkViewModel(
            new SttBenchService(Plan, bench, bench, folders, TimeProvider.System),
            Plan,
            _ => false,
            null,
            () => (SttEngine.RasrV2Fp32, SttMode.Accurate, SttDevice.Cpu),
            (_, _, _) => Task.CompletedTask,
            () => SttDevice.Cpu,
            TimeZoneInfo.Local
        );
    }

    private sealed class DesignerBench : ISttBenchRowRunner, ISttEngineSuspender
    {
        private const string NoBenchmark = "The designer's speech benchmark window has no benchmark behind it";

        public Task<SttBenchRowOutcome> RunRowAsync(SttBenchJob job, TimeSpan deadline, CancellationToken ct) =>
            throw new InvalidOperationException(NoBenchmark);

        public Task SuspendAsync(CancellationToken ct) => throw new InvalidOperationException(NoBenchmark);

        public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
