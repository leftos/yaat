using System.Formats.Tar;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SharpCompress.Compressors.BZip2;
using Yaat.Client.Logging;
using Yaat.Sim;
using SharpCompressionMode = SharpCompress.Compressors.CompressionMode;

namespace Yaat.Client.Services;

/// <summary>
/// Downloads and installs the Piper LibriTTS-R voice pack used by solo-training pilot TTS.
/// The archive is kept outside the app install directory so Velopack upgrades do not remove it.
/// Observable state mirrors the LM-Kit model and CUDA installer flows in Settings.
/// </summary>
public sealed partial class PiperVoiceInstaller : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger<PiperVoiceInstaller>();

    public const string VoiceName = "Piper LibriTTS-R medium";
    public const long ApproxDownloadBytes = 75L * 1024 * 1024;
    public const long ApproxDiskBytes = 340L * 1024 * 1024;

    private const string ArchiveFileName = "vits-piper-en_US-libritts_r-medium.tar.bz2";
    private const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-libritts_r-medium.tar.bz2";
    private const string PartialDirName = ".partial-piper-voice";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PiperVoiceInstaller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _isInstalled = IsInstalledOnDisk();
        _statusMessage = _isInstalled
            ? "Ready"
            : $"Not downloaded (~{ApproxDownloadBytes / (1024 * 1024)} MB download, ~{ApproxDiskBytes / (1024 * 1024)} MB on disk)";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusMessage = "";

    public bool CanInstall => !IsInstalled && !IsBusy;

    public bool CanUninstall => IsInstalled && !IsBusy;

    public static bool IsInstalledOnDisk() => PilotVoicePack.IsComplete(PilotVoicePack.InstallRoot);

    public async Task InstallAsync(CancellationToken ct)
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("Voice pack install already in progress.");
        }

        try
        {
            IsBusy = true;
            Progress = 0;
            StatusMessage = "Preparing...";
            await RunInstallAsync(ct).ConfigureAwait(false);
            IsInstalled = IsInstalledOnDisk();
            StatusMessage = IsInstalled ? "Ready" : "Install failed - voice pack is incomplete.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
            SafeDeletePartial();
            throw;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Piper voice pack install failed");
            StatusMessage = $"Failed: {ex.Message}";
            SafeDeletePartial();
            throw;
        }
        finally
        {
            IsBusy = false;
            _lock.Release();
        }
    }

    public void Uninstall()
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("Install in progress; cannot uninstall.");
        }

        try
        {
            if (Directory.Exists(PilotVoicePack.InstallRoot))
            {
                Directory.Delete(PilotVoicePack.InstallRoot, recursive: true);
                Log.LogInformation("Removed Piper voice pack at {Root}", PilotVoicePack.InstallRoot);
            }

            IsInstalled = false;
            Progress = 0;
            StatusMessage = "Removed.";
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RunInstallAsync(CancellationToken ct)
    {
        SafeDeletePartial();
        var partialRoot = Path.Combine(YaatPaths.Combine("voices"), PartialDirName);
        var archivePath = Path.Combine(partialRoot, ArchiveFileName);
        Directory.CreateDirectory(partialRoot);

        StatusMessage = "Downloading Piper voice pack...";
        await DownloadArchiveAsync(archivePath, ct).ConfigureAwait(false);

        StatusMessage = "Extracting Piper voice pack...";
        SetProgress(0.82);
        await Task.Run(() => ExtractArchive(archivePath, partialRoot, ct), ct).ConfigureAwait(false);
        SetProgress(0.95);

        var extractedVoiceDir =
            Directory.EnumerateDirectories(partialRoot, PilotVoicePack.DirectoryName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new DirectoryNotFoundException($"{PilotVoicePack.DirectoryName} missing from downloaded archive.");
        if (!PilotVoicePack.IsComplete(extractedVoiceDir))
        {
            throw new InvalidDataException("Downloaded voice pack is missing required Piper files.");
        }

        MaterializeFromPartial(extractedVoiceDir);
        SafeDeletePartial();
        SetProgress(1.00);
    }

    private async Task DownloadArchiveAsync(string archivePath, CancellationToken ct)
    {
        Log.LogInformation("Downloading Piper voice pack from {Url}", DownloadUrl);
        using var response = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var file = File.Create(archivePath);

        long bytesRead = 0;
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            bytesRead += read;
            if (total.HasValue && total.Value > 0)
            {
                var fraction = (double)bytesRead / total.Value;
                SetProgress(0.80 * fraction);
                StatusMessage = $"Downloading {fraction * 100:F0}% ({bytesRead / (1024 * 1024)} / {total.Value / (1024 * 1024)} MB)";
            }
            else
            {
                StatusMessage = $"Downloading {bytesRead / (1024 * 1024)} MB";
            }
        }
    }

    private static void ExtractArchive(string archivePath, string destRoot, CancellationToken ct)
    {
        using var file = File.OpenRead(archivePath);
        using var bzip = BZip2Stream.Create(file, SharpCompressionMode.Decompress, decompressConcatenated: false, leaveOpen: false);
        TarFile.ExtractToDirectory(bzip, destRoot, overwriteFiles: true);
        ct.ThrowIfCancellationRequested();
    }

    private static void MaterializeFromPartial(string extractedVoiceDir)
    {
        if (Directory.Exists(PilotVoicePack.InstallRoot))
        {
            Directory.Delete(PilotVoicePack.InstallRoot, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PilotVoicePack.InstallRoot)!);
        Directory.Move(extractedVoiceDir, PilotVoicePack.InstallRoot);
    }

    private static void SafeDeletePartial()
    {
        var partialRoot = Path.Combine(YaatPaths.Combine("voices"), PartialDirName);
        if (!Directory.Exists(partialRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(partialRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to clean partial Piper voice install dir at {Path}", partialRoot);
        }
    }

    private void SetProgress(double value)
    {
        Progress = Math.Round(Math.Clamp(value, 0.0, 1.0), 3);
    }
}
