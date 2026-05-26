using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// Speech-recognition debugger window. Master-detail layout:
/// <list type="bullet">
///   <item>Left: scrollable list of recent push-to-talk sessions (always populated).</item>
///   <item>Right: per-session detail — playback for sessions with saved audio, expandable
///         pipeline trace, scenario context snapshot, and Export / Delete actions.</item>
/// </list>
/// Opt-in capture is gated by <see cref="UserPreferences.SpeechSampleCaptureEnabled"/> — the
/// header pill makes the current state visible at a glance, and a Settings shortcut deep-links
/// to the right tab without forcing the user to navigate manually.
/// </summary>
public partial class SpeechDebugWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<SpeechDebugWindow>();

    private SpeechDebugViewModel? _viewModel;
    private UserPreferences? _preferences;
    private SpeechSampleStore? _sampleStore;
    private AudioCaptureService? _audioCapture;
    private WaveOutEvent? _waveOut;
    private WaveFileReader? _waveReader;

    public SpeechDebugWindow()
    {
        InitializeComponent();
        WireCommonButtons();
    }

    public SpeechDebugWindow(
        SpeechRecognitionService service,
        SpeechSampleStore sampleStore,
        UserPreferences preferences,
        AudioCaptureService? audioCapture = null
    )
        : this()
    {
        _preferences = preferences;
        _sampleStore = sampleStore;
        _audioCapture = audioCapture;

        _viewModel = new SpeechDebugViewModel(service, sampleStore, preferences);
        DataContext = _viewModel;

        new WindowGeometryHelper(this, preferences, "SpeechDebug", 1120, 720).Restore();

        Closed += (_, _) => DisposePlayback();
    }

    private void WireCommonButtons()
    {
        // Only wire buttons that live in the Window's main chrome here — the detail-pane buttons
        // (Play / Stop / Export this sample) are inside a DataTemplate and aren't present in the
        // visual tree at ctor time. Those are wired via Click="…" attributes in the XAML, which
        // Avalonia resolves against this class each time the template inflates.
        var closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => Close();
        }

        var settingsBtn = this.FindControl<Button>("SettingsButton");
        if (settingsBtn is not null)
        {
            settingsBtn.Click += OnSettingsClick;
        }

        var exportSelectedBtn = this.FindControl<Button>("ExportSelectedButton");
        if (exportSelectedBtn is not null)
        {
            exportSelectedBtn.Click += OnExportSelectedClick;
        }
    }

    private void OnSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_preferences is null)
        {
            return;
        }
        var dialog = new SettingsWindow(_preferences, _audioCapture, _sampleStore);
        dialog.Show(this);
    }

    public void OnPlayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = _viewModel?.SelectedRow?.Sample?.AudioPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        DisposePlayback();

        try
        {
            _waveReader = new WaveFileReader(path);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_waveReader);
            _waveOut.PlaybackStopped += (_, args) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SetPlaybackStatus(args.Exception is null ? string.Empty : $"Playback error: {args.Exception.Message}");
                    DisposePlayback();
                });
            };
            _waveOut.Play();
            SetPlaybackStatus($"Playing… ({_waveReader.TotalTime.TotalSeconds:F1}s)");
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to play speech sample {Path}", path);
            SetPlaybackStatus("Playback failed (see log)");
            DisposePlayback();
        }
    }

    public void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _waveOut?.Stop();
        SetPlaybackStatus(string.Empty);
    }

    public async void OnExportSingleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel?.SelectedRow?.Sample is null)
        {
            return;
        }

        var sample = _viewModel.SelectedRow.Sample;
        var path = await PromptForBundlePath($"{sample.Id}.yaat-speech-sample.zip");
        if (path is null)
        {
            return;
        }

        var ok = _viewModel.ExportSingle(path);
        if (!ok)
        {
            Log.LogWarning("Single-sample export to {Path} returned false", path);
        }
    }

    private async void OnExportSelectedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.SelectedForExportCount == 0)
        {
            return;
        }

        var defaultName = $"yaat-speech-samples-{DateTime.UtcNow:yyyyMMdd-HHmmss}.yaat-speech-sample.zip";
        var path = await PromptForBundlePath(defaultName);
        if (path is null)
        {
            return;
        }

        var written = _viewModel.ExportSelectedBundle(path);
        Log.LogInformation("Exported {Count} speech samples to {Path}", written, path);
    }

    private async Task<string?> PromptForBundlePath(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save speech sample bundle",
                SuggestedFileName = suggestedName,
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("YAAT speech sample") { Patterns = ["*.yaat-speech-sample.zip", "*.zip"] }],
            }
        );

        var path = file?.TryGetLocalPath();
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private void SetPlaybackStatus(string text)
    {
        if (_viewModel is not null)
        {
            _viewModel.PlaybackStatus = text;
        }
    }

    private void DisposePlayback()
    {
        try
        {
            _waveOut?.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "WaveOut dispose threw");
        }
        try
        {
            _waveReader?.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "WaveFileReader dispose threw");
        }
        _waveOut = null;
        _waveReader = null;
    }
}
