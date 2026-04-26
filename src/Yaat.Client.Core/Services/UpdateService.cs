using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Checks for app updates via GitHub Releases using Velopack. Both Yaat.Client and
/// Yaat.VStrips construct their own UpdateService with distinct Velopack channels
/// so each app downloads its own installer from a shared GitHub release.
/// </summary>
public sealed class UpdateService
{
    private static readonly ILogger Log = AppLog.CreateLogger("UpdateService");

    private readonly UpdateManager _updateManager;

    /// <summary>
    /// Constructs an updater that reads release metadata from leftos/yaat releases.
    /// Pass an explicit channel (e.g., "vstrips-win") for apps packed with a
    /// non-default Velopack channel; pass null to use the platform default channel
    /// ("win"/"osx"/"linux"), which is what Yaat.Client uses.
    /// </summary>
    public UpdateService(string? channel)
    {
        var source = new GithubSource("https://github.com/leftos/yaat", accessToken: null, prerelease: true);
        var options = channel is null ? null : new UpdateOptions { ExplicitChannel = channel };
        _updateManager = new UpdateManager(source, options);
    }

    public bool IsInstalled => _updateManager.IsInstalled;

    public string? CurrentVersion => _updateManager.IsInstalled ? _updateManager.CurrentVersion?.ToString() : null;

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        if (!_updateManager.IsInstalled)
        {
            Log.LogDebug("App is not installed via Velopack — skipping update check");
            return null;
        }

        try
        {
            var update = await _updateManager.CheckForUpdatesAsync();
            if (update is not null)
            {
                Log.LogInformation("Update available: {Version}", update.TargetFullRelease.Version);
            }
            else
            {
                Log.LogDebug("No updates available");
            }

            return update;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to check for updates");
            return null;
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
