using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;

namespace Yaat.Client.Services.Speech;

/// <summary>One file of an STT model, pinned by size and SHA-256.</summary>
/// <param name="Name">The file's name in the model folder; a plain file name, never a path.</param>
/// <param name="Urls">Where the file is served, tried in order until one serves the pinned bytes.</param>
/// <param name="Size">The file's size in bytes.</param>
/// <param name="Sha256">The file's SHA-256, lower-case hex.</param>
public sealed record SttModelFile(string Name, IReadOnlyList<string> Urls, long Size, string Sha256);

/// <summary>One STT model of the download manifest's <c>models[]</c>: its id and the files it is built from.</summary>
/// <param name="Id">The model's id, which also names its folder under the install root.</param>
/// <param name="Files">The files the model folder holds, flat.</param>
public sealed record SttModelDownload(string Id, IReadOnlyList<SttModelFile> Files)
{
    /// <summary>The bytes the model downloads: the sum of every file's size.</summary>
    public long TotalBytes => Files.Sum(f => f.Size);
}

/// <summary>
/// Downloads, verifies and removes one STT model listed in <c>stt-downloads.json</c>'s <c>models[]</c>, each file pinned
/// by size and SHA-256.
///
/// Layout after a successful install:
/// <code>
///   &lt;installRoot&gt;/
///       &lt;id&gt;/
///           .installed      (sentinel: one "&lt;Name&gt; &lt;Sha256&gt;" line per file)
///           &lt;every file, flat under its manifest name&gt;
/// </code>
/// Downloads stream to a fresh <c>&lt;installRoot&gt;/&lt;id&gt;.staging-&lt;random&gt;/</c> folder, which gets the sentinel last
/// and is then swapped in for the model folder; the installed model stays in place until the new one is complete, so a
/// failed or cancelled install leaves it as it was.
///
/// One writer per install root, across instances and processes: install and delete hold the lock file
/// <c>&lt;installRoot&gt;.lock</c> beside the root (<see cref="TryLockRoot"/>). The observable properties are set from the
/// worker thread; the caller marshals them to the UI.
/// </summary>
/// <param name="http">The client the files are downloaded with.</param>
/// <param name="installRoot">The folder models are installed under; production passes <see cref="InstallRootDefault"/>.</param>
/// <param name="model">The model this installer manages, usually from <see cref="ReadModel"/>.</param>
/// <param name="timeProvider">The clock the body idle timeout runs on; production passes <see cref="TimeProvider.System"/>.</param>
public sealed partial class SttModelInstaller(HttpClient http, string installRoot, SttModelDownload model, TimeProvider timeProvider)
    : ObservableObject
{
    private static readonly ILogger Log = AppLog.CreateLogger<SttModelInstaller>();

    private const string InstalledSentinelFileName = ".installed";
    private const string StagingDirInfix = ".staging-";
    private const string OldDirInfix = ".old-";
    private const string ReadyMessage = "Ready";
    private const string VerifyingMessage = "Verifying...";
    private const string CancelledMessage = "Cancelled.";
    private const string RemovedMessage = "Removed.";
    private const string BusyMessage = "Another YAAT is downloading or removing this model.";
    private const string RefusalKey = "SttModelInstaller.Refusal";
    private const double BytesPerMb = 1024 * 1024;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int LinuxEWouldBlock = 11;
    private const int MacOsEWouldBlock = 35;

    /// <summary>Characters no model id or file name may hold on any platform: the host's invalid ones plus both separators and the drive colon.</summary>
    private static readonly char[] UnsafeNameChars = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':'];

    /// <summary>Windows device names, which name a device instead of a file with or without an extension.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
        StringComparer.OrdinalIgnoreCase
    );

    private readonly VerifiedFileDownloader _downloader = new(http, timeProvider);
    private readonly string _installRoot = installRoot;
    private readonly SttModelDownload _model = model;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Lock _cancelGate = new();
    private CancellationTokenSource? _installCts;

    [ObservableProperty]
    private bool _isInstalled = RecoverAndCheckInstalled(installRoot, model);

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>0.0 to 1.0 across the whole model, weighted by size. Set from the install worker thread.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>Short status text for the UI.</summary>
    [ObservableProperty]
    private string _statusMessage = IsInstalledOnDisk(installRoot, model) ? ReadyMessage : $"Not downloaded (~{TotalMb(model):N0} MB)";

    /// <summary>The production install root, outside the Velopack-managed app folder so upgrades keep it.</summary>
    public static string InstallRootDefault => YaatPaths.Combine("models", "stt");

    /// <summary>The folder model <paramref name="id"/> is installed in under <paramref name="installRoot"/>.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="id">The model's id.</param>
    public static string ModelDir(string installRoot, string id) => Path.Combine(installRoot, id);

    /// <summary>Every model the manifest's <c>models[]</c> lists, in manifest order.</summary>
    /// <param name="manifestPath">The <c>stt-downloads.json</c> that pins the models.</param>
    /// <exception cref="InvalidDataException">The manifest has no valid <c>models[]</c>.</exception>
    /// <exception cref="IOException">The manifest could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The manifest could not be read.</exception>
    public static IReadOnlyList<SttModelDownload> ReadModels(string manifestPath)
    {
        try
        {
            return ParseModels(manifestPath);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{manifestPath} is not a valid STT download manifest: {ex.Message}", ex);
        }
    }

    /// <summary>The model <paramref name="id"/> of the manifest's <c>models[]</c>.</summary>
    /// <param name="manifestPath">The <c>stt-downloads.json</c> that pins the models.</param>
    /// <param name="id">The model's id.</param>
    /// <exception cref="InvalidDataException">The manifest has no valid <c>models[]</c>, or lists no model <paramref name="id"/>.</exception>
    /// <exception cref="IOException">The manifest could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The manifest could not be read.</exception>
    public static SttModelDownload ReadModel(string manifestPath, string id) =>
        ReadModels(manifestPath).FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"{manifestPath} lists no STT model {id}");

    /// <summary>
    /// True when <paramref name="model"/>'s folder holds a sentinel naming exactly its files and hashes, and every file at
    /// its pinned size. No file is hashed. Safe from any thread.
    /// </summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="model">The model to check.</param>
    public static bool IsInstalledOnDisk(string installRoot, SttModelDownload model) => FolderHoldsModel(ModelDir(installRoot, model.Id), model);

    /// <summary>
    /// Takes the install root's writer lock: the file <c>&lt;installRoot&gt;.lock</c> beside the root, opened exclusively,
    /// so one writer at a time installs or removes a model, across instances and processes. The lock file is never deleted.
    /// </summary>
    /// <param name="installRoot">The install root to lock.</param>
    /// <returns>The open lock, which the caller disposes to release it; null when another holder has it.</returns>
    public static FileStream? TryLockRoot(string installRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        string parent =
            Path.GetDirectoryName(root)
            ?? throw new ArgumentException($"The STT model install root {root} has no parent folder to hold its lock", nameof(installRoot));
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(parent, Path.GetFileName(root) + ".lock");
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            Log.LogInformation("The STT model install lock {Path} is held by another writer", lockPath);
            return null;
        }
    }

    /// <summary>
    /// Downloads and installs the model. Single-flight per instance, and one writer per install root. A failed or
    /// cancelled install leaves no staging folder and any installed model as it was.
    /// </summary>
    /// <param name="ct">Cancels the install, as <see cref="Cancel"/> does.</param>
    /// <exception cref="InvalidOperationException">An install or delete is already running, or another writer holds the root.</exception>
    /// <exception cref="OperationCanceledException">The install was cancelled.</exception>
    /// <exception cref="InvalidDataException">A file failed its size or SHA-256 check at every URL.</exception>
    /// <exception cref="HttpRequestException">Every URL of a file failed, the last with an HTTP error or a timeout.</exception>
    public async Task InstallAsync(CancellationToken ct)
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("An install or delete of this model is already running.");
        }
        string? stagingDir = null;
        FileStream? rootLock = null;
        CancellationTokenSource? cts = null;
        try
        {
            stagingDir = Path.Combine(_installRoot, _model.Id + StagingDirInfix + Guid.NewGuid().ToString("N"));
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            SetInstallCts(cts);
            IsBusy = true;
            Progress = 0;
            rootLock = LockRootOrRefuse();
            ReportDownloaded(0);
            await RunInstallAsync(stagingDir, cts.Token).ConfigureAwait(false);
            StatusMessage = ReadyMessage;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("STT model {Id} install cancelled", _model.Id);
            StatusMessage = CancelledMessage;
            DeleteDirBestEffort(stagingDir);
            throw;
        }
        catch (Exception ex) when (!ex.Data.Contains(RefusalKey))
        {
            Log.LogError(ex, "STT model {Id} install failed", _model.Id);
            StatusMessage = $"Failed: {ex.Message}";
            DeleteDirBestEffort(stagingDir);
            throw;
        }
        finally
        {
            SetInstallCts(null);
            cts?.Dispose();
            rootLock?.Dispose();
            IsBusy = false;
            _lock.Release();
        }
    }

    /// <summary>
    /// Cancels a running install; harmless when none is running. A Cancel after the swap begins lets the install finish.
    /// </summary>
    public void Cancel()
    {
        lock (_cancelGate)
        {
            _installCts?.Cancel();
        }
    }

    /// <summary>
    /// Removes the installed model, sentinel first, then its folder, then any staging or old-model folder an earlier run of
    /// it left under the root. Harmless if nothing is installed.
    /// </summary>
    /// <exception cref="InvalidOperationException">An install or delete is running, or another writer holds the root.</exception>
    /// <exception cref="IOException">A file of the model could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file of the model could not be deleted.</exception>
    public async Task DeleteAsync()
    {
        if (!_lock.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("An install or delete of this model is already running.");
        }
        FileStream? rootLock = null;
        try
        {
            IsBusy = true;
            try
            {
                rootLock = LockRootOrRefuse();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogError(ex, "Locking the STT model root {Root} to remove {Id} failed", _installRoot, _model.Id);
                StatusMessage = $"Failed: {ex.Message}";
                throw;
            }
            await Task.Run(RemoveModel).ConfigureAwait(false);
            Progress = 0;
            StatusMessage = RemovedMessage;
        }
        finally
        {
            rootLock?.Dispose();
            IsBusy = false;
            _lock.Release();
        }
    }

    private static double TotalMb(SttModelDownload model) => Math.Round(model.TotalBytes / BytesPerMb);

    private static string SentinelPath(string modelDir) => Path.Combine(modelDir, InstalledSentinelFileName);

    private static string ExpectedSentinelContent(SttModelDownload model) => string.Join('\n', model.Files.Select(f => $"{f.Name} {f.Sha256}"));

    /// <summary>
    /// True when <paramref name="dir"/> holds a sentinel naming exactly <paramref name="model"/>'s files and hashes, and
    /// every file at its pinned size. No file is hashed.
    /// </summary>
    private static bool FolderHoldsModel(string dir, SttModelDownload model)
    {
        string sentinelPath = SentinelPath(dir);
        try
        {
            if (
                !File.Exists(sentinelPath) || !string.Equals(File.ReadAllText(sentinelPath), ExpectedSentinelContent(model), StringComparison.Ordinal)
            )
            {
                return false;
            }
            return model.Files.All(file =>
            {
                var info = new FileInfo(Path.Combine(dir, file.Name));
                return info.Exists && (info.Length == file.Size);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Failed to check the STT model {Id} at {Dir}", model.Id, dir);
            return false;
        }
    }

    /// <summary>
    /// Puts an orphaned old-model folder back (see <see cref="RestoreOrphanedOldModel"/>) when the root lock is free, then
    /// reports whether the model is installed.
    /// </summary>
    private static bool RecoverAndCheckInstalled(string installRoot, SttModelDownload model)
    {
        if (Directory.Exists(installRoot) && !Directory.Exists(ModelDir(installRoot, model.Id)))
        {
            try
            {
                using FileStream? rootLock = TryLockRoot(installRoot);
                if (rootLock is not null)
                {
                    RestoreOrphanedOldModel(installRoot, model);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning(ex, "Failed to lock the STT model root {Root} to recover {Id}", installRoot, model.Id);
            }
        }
        return IsInstalledOnDisk(installRoot, model);
    }

    /// <summary>
    /// When the model folder is missing and exactly one <c>&lt;id&gt;.old-*</c> folder holds the complete model (a swap that
    /// could not move it back left it there), moves that folder back as the model folder. The caller holds the root lock.
    /// </summary>
    private static void RestoreOrphanedOldModel(string installRoot, SttModelDownload model)
    {
        string modelDir = ModelDir(installRoot, model.Id);
        try
        {
            if (!Directory.Exists(installRoot) || Directory.Exists(modelDir))
            {
                return;
            }
            List<string> complete =
            [
                .. Directory.EnumerateDirectories(installRoot, model.Id + OldDirInfix + "*").Where(d => FolderHoldsModel(d, model)),
            ];
            if (complete.Count != 1)
            {
                if (complete.Count > 1)
                {
                    Log.LogWarning(
                        "Not restoring the STT model {Id}: {Count} complete old-model folders are under {Root}",
                        model.Id,
                        complete.Count,
                        installRoot
                    );
                }
                return;
            }
            Directory.Move(complete[0], modelDir);
            Log.LogInformation("Restored the STT model {Id} from the orphaned {OldDir}", model.Id, complete[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Failed to restore the orphaned old STT model {Id} under {Root}", model.Id, installRoot);
        }
    }

    private static List<SttModelDownload> ParseModels(string manifestPath)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        string baseUrl = RequiredString(root, "baseUrl", manifestPath);
        List<SttModelDownload> models = [.. root.GetProperty("models").EnumerateArray().Select(m => ParseModel(m, baseUrl, manifestPath))];
        if (models.Count == 0)
        {
            throw new InvalidDataException($"{manifestPath} lists no STT models");
        }
        return models;
    }

    private static SttModelDownload ParseModel(JsonElement model, string baseUrl, string manifestPath)
    {
        string id = PlainName(RequiredString(model, "id", manifestPath), manifestPath);
        List<SttModelFile> files = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement file in model.GetProperty("files").EnumerateArray())
        {
            string name = PlainName(RequiredString(file, "name", manifestPath), manifestPath);
            if (string.Equals(name, InstalledSentinelFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{manifestPath} names a file of STT model {id} '{name}', which is the sentinel's name");
            }
            if (!names.Add(name))
            {
                throw new InvalidDataException($"{manifestPath} lists the file '{name}' of STT model {id} more than once");
            }
            string key = RequiredString(file, "key", manifestPath);
            string sha256 = RequiredString(file, "sha256", manifestPath);
            files.Add(new SttModelFile(name, [baseUrl + key], file.GetProperty("size").GetInt64(), sha256));
        }
        if (files.Count == 0)
        {
            throw new InvalidDataException($"{manifestPath} lists no files for STT model {id}");
        }
        return new SttModelDownload(id, files);
    }

    private static string RequiredString(JsonElement element, string property, string manifestPath) =>
        element.GetProperty(property).GetString() ?? throw new InvalidDataException($"{manifestPath} has a null {property}");

    /// <summary>Returns <paramref name="name"/> when it is a plain file or folder name, so it cannot point outside the install root.</summary>
    private static string PlainName(string name, string manifestPath)
    {
        bool unsafeName =
            (name.Length == 0)
            || (name == ".")
            || (name == "..")
            || (Path.GetFileName(name) != name)
            || Path.IsPathRooted(name)
            || (name.IndexOfAny(UnsafeNameChars) >= 0)
            || IsReservedDeviceName(name);
        if (unsafeName)
        {
            throw new InvalidDataException($"{manifestPath} names '{name}', which is not a plain file name");
        }
        return name;
    }

    /// <summary>True when <paramref name="name"/>, up to its first dot, is a Windows device name (<c>NUL</c>, <c>con.txt</c>).</summary>
    private static bool IsReservedDeviceName(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return ReservedDeviceNames.Contains((dot < 0) ? name : name[..dot]);
    }

    private static bool IsSharingViolation(IOException ex)
    {
        if (OperatingSystem.IsWindows())
        {
            int code = ex.HResult & 0xFFFF;
            return (code == ErrorSharingViolation) || (code == ErrorLockViolation);
        }
        return ex.HResult == (OperatingSystem.IsMacOS() ? MacOsEWouldBlock : LinuxEWouldBlock);
    }

    private void SetInstallCts(CancellationTokenSource? cts)
    {
        lock (_cancelGate)
        {
            _installCts = cts;
        }
    }

    private FileStream LockRootOrRefuse()
    {
        FileStream? rootLock = TryLockRoot(_installRoot);
        if (rootLock is not null)
        {
            return rootLock;
        }
        Log.LogWarning("Another writer holds the STT model install root {Root}; refusing", _installRoot);
        StatusMessage = BusyMessage;
        var refusal = new InvalidOperationException(BusyMessage);
        refusal.Data[RefusalKey] = true;
        throw refusal;
    }

    private async Task RunInstallAsync(string stagingDir, CancellationToken ct)
    {
        RestoreOrphanedOldModel(_installRoot, _model);
        SweepLeftovers();
        Directory.CreateDirectory(stagingDir);

        long doneBytes = 0;
        foreach (SttModelFile file in _model.Files)
        {
            long before = doneBytes;
            var verified = new VerifiedFile(file.Name, file.Urls, file.Size, file.Sha256);
            await _downloader
                .DownloadAsync(
                    verified,
                    Path.Combine(stagingDir, file.Name),
                    bytesRead => ReportDownloaded(before + Math.Min(bytesRead, file.Size)),
                    ct
                )
                .ConfigureAwait(false);
            doneBytes += file.Size;
        }

        ct.ThrowIfCancellationRequested();
        StatusMessage = VerifyingMessage;
        File.WriteAllText(SentinelPath(stagingDir), ExpectedSentinelContent(_model));
        if (!FolderHoldsModel(stagingDir, _model))
        {
            throw new InvalidDataException($"The staged STT model {_model.Id} at {stagingDir} is incomplete; the installed model is left as it was");
        }
        SwapIn(stagingDir);
        IsInstalled = true;
        SetProgress(1.0);
    }

    private void ReportDownloaded(long readBytes)
    {
        double fraction = Math.Min(1.0, readBytes / (double)Math.Max(1, _model.TotalBytes));
        SetProgress(fraction);
        StatusMessage = $"Downloading {fraction * 100:F0}% ({readBytes / BytesPerMb:N0} / {TotalMb(_model):N0} MB)";
    }

    /// <summary>
    /// Deletes the staging and old-model folders an earlier run of this model left under the root. An old-model folder is
    /// kept while the model folder is missing, since a swap that could not restore leaves the only good copy there.
    /// </summary>
    private void SweepLeftovers()
    {
        if (!Directory.Exists(_installRoot))
        {
            return;
        }
        List<string> leftovers = [.. Directory.EnumerateDirectories(_installRoot, _model.Id + StagingDirInfix + "*")];
        List<string> oldModels = [.. Directory.EnumerateDirectories(_installRoot, _model.Id + OldDirInfix + "*")];
        if (Directory.Exists(ModelDir(_installRoot, _model.Id)))
        {
            leftovers.AddRange(oldModels);
        }
        else
        {
            foreach (string dir in oldModels)
            {
                Log.LogWarning("Keeping {Dir}: no model folder is beside it, so it may hold the only good copy", dir);
            }
        }
        foreach (string dir in leftovers)
        {
            DeleteDirBestEffort(dir);
        }
    }

    /// <summary>
    /// Moves the installed model aside, moves the staged one in and deletes the old one. A failure moves the old model back
    /// and rethrows; a failure to delete the replaced model afterwards only logs.
    /// </summary>
    private void SwapIn(string stagingDir)
    {
        string modelDir = ModelDir(_installRoot, _model.Id);
        string oldDir = Path.Combine(_installRoot, _model.Id + OldDirInfix + Guid.NewGuid().ToString("N"));
        bool oldMoved = false;
        try
        {
            if (Directory.Exists(modelDir))
            {
                Directory.Move(modelDir, oldDir);
                oldMoved = true;
            }
            Directory.Move(stagingDir, modelDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(ex, "Swapping in the STT model {Id} at {Dir} failed; restoring the old model", _model.Id, modelDir);
            if (oldMoved)
            {
                RestoreOldModel(oldDir, modelDir);
            }
            throw;
        }

        DeleteDirBestEffort(oldDir);
    }

    private void RestoreOldModel(string oldDir, string modelDir)
    {
        try
        {
            Directory.Move(oldDir, modelDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(ex, "Failed to move the old STT model {Id} back; it is left at {OldDir}", _model.Id, oldDir);
        }
    }

    /// <summary>The staging and old-model folders of this model under the root.</summary>
    private List<string> LeftoverDirs()
    {
        if (!Directory.Exists(_installRoot))
        {
            return [];
        }
        return
        [
            .. Directory.EnumerateDirectories(_installRoot, _model.Id + StagingDirInfix + "*"),
            .. Directory.EnumerateDirectories(_installRoot, _model.Id + OldDirInfix + "*"),
        ];
    }

    private void RemoveModel()
    {
        string modelDir = ModelDir(_installRoot, _model.Id);
        try
        {
            string sentinelPath = SentinelPath(modelDir);
            if (File.Exists(sentinelPath))
            {
                File.Delete(sentinelPath);
            }
            IsInstalled = false;
            if (Directory.Exists(modelDir))
            {
                Directory.Delete(modelDir, recursive: true);
            }
            foreach (string leftover in LeftoverDirs())
            {
                Directory.Delete(leftover, recursive: true);
            }
            Log.LogInformation("Removed the STT model {Id} at {Dir}", _model.Id, modelDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(ex, "Failed to remove the STT model {Id} at {Dir}", _model.Id, modelDir);
            StatusMessage = $"Failed: {ex.Message}";
            IsInstalled = IsInstalledOnDisk(_installRoot, _model);
            throw;
        }
    }

    private static void DeleteDirBestEffort(string? path)
    {
        if ((path is null) || !Directory.Exists(path))
        {
            return;
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Failed to clean the STT model folder {Path}; the next install sweeps it", path);
        }
    }

    private void SetProgress(double value) => Progress = Math.Round(Math.Clamp(value, 0.0, 1.0), 3);
}
