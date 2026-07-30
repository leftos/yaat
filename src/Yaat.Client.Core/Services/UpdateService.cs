using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// How an update check ended. The startup check ignores everything but
/// <see cref="UpdateAvailable"/>; a user-invoked check reports each case.
/// </summary>
public enum UpdateCheckOutcome
{
    /// <summary>A newer release is ready to download.</summary>
    UpdateAvailable,

    /// <summary>This build is the newest release on its channel.</summary>
    UpToDate,

    /// <summary>Running portable or from source, so Velopack has no install to update.</summary>
    NotInstalled,

    /// <summary>The check threw — offline, GitHub unreachable, or unreadable release metadata.</summary>
    Failed,
}

/// <summary>
/// Result of an update check. <see cref="Update"/> is non-null only for
/// <see cref="UpdateCheckOutcome.UpdateAvailable"/>, <see cref="ErrorMessage"/> only for
/// <see cref="UpdateCheckOutcome.Failed"/>.
/// </summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateInfo? Update, string? ErrorMessage)
{
    public static UpdateCheckResult Available(UpdateInfo update) => new(UpdateCheckOutcome.UpdateAvailable, update, null);

    public static UpdateCheckResult UpToDate() => new(UpdateCheckOutcome.UpToDate, null, null);

    public static UpdateCheckResult NotInstalled() => new(UpdateCheckOutcome.NotInstalled, null, null);

    public static UpdateCheckResult Failed(string error) => new(UpdateCheckOutcome.Failed, null, error);
}

/// <summary>
/// Checks for app updates via GitHub Releases using Velopack.
/// </summary>
public sealed class UpdateService
{
    private static readonly ILogger Log = AppLog.CreateLogger("UpdateService");

    private readonly UpdateManager _updateManager;

    /// <summary>
    /// Constructs an updater that reads release metadata from leftos/yaat releases.
    /// Pass an explicit channel for apps packed with a non-default Velopack channel;
    /// pass null to use the platform default channel ("win"/"osx"/"linux").
    /// </summary>
    public UpdateService(string? channel)
    {
        var source = new GithubSource("https://github.com/leftos/yaat", accessToken: null, prerelease: true);
        var options = channel is null ? null : new UpdateOptions { ExplicitChannel = channel };
        _updateManager = new UpdateManager(source, options);
    }

    public bool IsInstalled => _updateManager.IsInstalled;

    public string? CurrentVersion => _updateManager.IsInstalled ? _updateManager.CurrentVersion?.ToString() : null;

    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        if (!_updateManager.IsInstalled)
        {
            Log.LogDebug("App is not installed via Velopack — skipping update check");
            return UpdateCheckResult.NotInstalled();
        }

        try
        {
            var update = await _updateManager.CheckForUpdatesAsync();
            if (update is null)
            {
                Log.LogDebug("No updates available");
                return UpdateCheckResult.UpToDate();
            }

            Log.LogInformation("Update available: {Version}", update.TargetFullRelease.Version);
            return UpdateCheckResult.Available(update);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to check for updates");
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    public async Task DownloadUpdateAsync(UpdateInfo info, Action<int> progress)
    {
        Log.LogInformation("Downloading update {Version}", info.TargetFullRelease.Version);
        await _updateManager.DownloadUpdatesAsync(info, progress);
        Log.LogInformation("Download complete for {Version}", info.TargetFullRelease.Version);
    }

    public void ApplyUpdateAndRestart(UpdateInfo info)
    {
        Log.LogInformation("Applying update {Version} and restarting", info.TargetFullRelease.Version);
        _updateManager.ApplyUpdatesAndRestart(info);
    }
}
