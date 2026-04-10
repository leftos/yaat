using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Checks for app updates via GitHub Releases using Velopack.
/// </summary>
public sealed class UpdateService
{
    private static readonly ILogger Log = AppLog.CreateLogger("UpdateService");

    private readonly UpdateManager _updateManager;

    public UpdateService()
    {
        _updateManager = new UpdateManager(new GithubSource("https://github.com/leftos/yaat", accessToken: null, prerelease: true));
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
