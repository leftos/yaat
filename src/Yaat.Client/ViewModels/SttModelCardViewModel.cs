using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One card of the Settings > Speech model picker: a model of the download manifest, with its download state and commands, or
/// the legacy Whisper engine, which downloads its sizes from LM-Kit elsewhere.
/// </summary>
public sealed partial class SttModelCardViewModel : ObservableObject, IDisposable
{
    /// <summary>The measured line every card shows until the benchmark exists.</summary>
    public const string NotMeasuredText = "Not yet: run the benchmark";

    private const string LicenceFileName = "LICENSE";

    private static readonly ILogger Log = AppLog.CreateLogger<SttModelCardViewModel>();

    /// <summary>The upstream page (the published model card) each manifest model's licence link opens before the model is downloaded.</summary>
    private static readonly Dictionary<string, string> UpstreamPages = new(StringComparer.Ordinal)
    {
        ["rasr-parakeet-v2-fp32"] = "https://huggingface.co/twangodev/rasr-parakeet-v2",
        ["rasr-parakeet-v2-int8"] = "https://huggingface.co/twangodev/rasr-parakeet-v2",
    };

    private readonly SttModelDownload? _model;
    private readonly SttModelInstaller? _installer;
    private readonly string _modelRoot;

    [ObservableProperty]
    private bool _isLoading;

    private SttModelCardViewModel(
        SttEngine engine,
        string title,
        string tag,
        string sizeText,
        SttModelDownload? model,
        SttModelInstaller? installer,
        string modelRoot,
        UiThreadMirror? install
    )
    {
        Engine = engine;
        Title = title;
        Tag = tag;
        SizeText = sizeText;
        _model = model;
        _installer = installer;
        _modelRoot = modelRoot;
        Install = install;
        install?.PropertyChanged += OnInstallChanged;
    }

    /// <summary>The engine this card selects.</summary>
    public SttEngine Engine { get; }

    /// <summary>The card's name: <c>rasr-parakeet v2 · fp32</c>.</summary>
    public string Title { get; }

    /// <summary>The subtle tag beside the name: <c>Default</c>, <c>Legacy</c>, or empty.</summary>
    public string Tag { get; }

    /// <summary>The download size: <c>2.55 GB</c>.</summary>
    public string SizeText { get; }

    /// <summary>The measured latency and memory line.</summary>
    public string MeasuredText => NotMeasuredText;

    /// <summary>Whether this is the legacy Whisper card, which has no Accurate mode and no download here.</summary>
    public bool IsWhisper => Engine == SttEngine.Whisper;

    /// <summary>Whether the card carries "Built with Llama": the rasr models, released under the Llama 3.2 Community License.</summary>
    public bool UsesLlama => !IsWhisper;

    /// <summary>The model installer's state on the UI thread; null for Whisper.</summary>
    public UiThreadMirror? Install { get; }

    /// <summary>The state line: <c>Ready (2.55 GB on disk)</c>, <c>1.1 of 2.55 GB</c> while downloading, else the size; empty for Whisper.</summary>
    public string StateText =>
        ((Install is null) || (_model is null)) ? ""
        : Install.IsInstalled ? SttSizeText.Ready(_model.TotalBytes)
        : Install.IsBusy ? SttSizeText.Progress(Install.Progress, _model.TotalBytes)
        : SizeText;

    /// <summary>A card for a manifest model, its installer mirrored with <paramref name="post"/>.</summary>
    /// <param name="engine">The engine the model runs as.</param>
    /// <param name="model">The manifest model.</param>
    /// <param name="installer">The model's app-lifetime installer.</param>
    /// <param name="modelRoot">The folder the model installs under.</param>
    /// <param name="isDefault">Whether this is the engine fresh preferences select.</param>
    /// <param name="post">Queues a callback on the UI thread.</param>
    public static SttModelCardViewModel ForModel(
        SttEngine engine,
        SttModelDownload model,
        SttModelInstaller installer,
        string modelRoot,
        bool isDefault,
        Action<Action> post
    ) =>
        new(
            engine,
            TitleOf(engine),
            isDefault ? "Default" : "",
            SttSizeText.Total(model.TotalBytes),
            model,
            installer,
            modelRoot,
            UiThreadMirror.Of(installer, post)
        );

    /// <summary>The legacy Whisper card.</summary>
    public static SttModelCardViewModel ForWhisper() =>
        new(SttEngine.Whisper, TitleOf(SttEngine.Whisper), "Legacy", "Per Whisper size, from LM-Kit", null, null, "", null);

    /// <summary>Stops mirroring the installer, which keeps running after Settings closes.</summary>
    public void Dispose()
    {
        if (Install is not null)
        {
            Install.PropertyChanged -= OnInstallChanged;
            Install.Dispose();
        }
    }

    private static string TitleOf(SttEngine engine) =>
        engine switch
        {
            SttEngine.RasrV2Fp32 => "rasr-parakeet v2 · fp32",
            SttEngine.RasrV2Int8 => "rasr-parakeet v2 · int8",
            SttEngine.Whisper => "Whisper (LM-Kit)",
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown speech-recognition engine"),
        };

    private bool CanDownload() => Install is { IsBusy: false, IsInstalled: false };

    private bool CanCancelDownload() => Install is { IsBusy: true };

    private bool CanDelete() => Install is { IsBusy: false, IsInstalled: true };

    private bool CanViewLicence() => (_model is not null) && ((Install?.IsInstalled == true) || UpstreamPages.ContainsKey(_model.Id));

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (_installer is null)
        {
            return;
        }
        // No token of this card's: the installer's own Cancel stops the download from any later Settings window too.
        try
        {
            await _installer.InstallAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("The {Model} download was cancelled", Title);
        }
        catch (Exception ex)
        {
            // The installer logged the failure (or the refusal) and set its StatusMessage, which the card shows.
            Log.LogWarning(ex, "The {Model} download did not complete", Title);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload() => _installer?.Cancel();

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (_installer is null)
        {
            return;
        }
        try
        {
            await _installer.DeleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Removing the {Model} model failed", Title);
        }
    }

    [RelayCommand(CanExecute = nameof(CanViewLicence))]
    private void ViewLicence()
    {
        if (_model is null)
        {
            return;
        }
        string? page = UpstreamPages.GetValueOrDefault(_model.Id);
        if (Install?.IsInstalled == true)
        {
            string licence = Path.Combine(SttModelInstaller.ModelDir(_modelRoot, _model.Id), LicenceFileName);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = licence, UseShellExecute = true });
                return;
            }
            catch (Win32Exception ex)
            {
                // LICENSE has no extension, so the shell may have nothing to open it with; the upstream page carries the licence too.
                Log.LogWarning(ex, "Opening the {Model} licence {Target} failed; opening its upstream page instead", Title, licence);
            }
        }
        if (page is not null)
        {
            ShellOpen(page);
        }
    }

    private void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Opening the {Model} licence {Target} failed", Title, target);
        }
    }

    private void OnInstallChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(StateText));
        DownloadCommand.NotifyCanExecuteChanged();
        CancelDownloadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ViewLicenceCommand.NotifyCanExecuteChanged();
    }
}
