using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

// The resync: the ARTCC's server scenarios downloaded into the store off the UI thread, then the rows rebuilt from it.
// The status line and notices use Load Scenario's wording.
public sealed partial class ScenarioBrowserViewModel
{
    /// <summary>The status line and the notice lines for a finished download that reached the server.</summary>
    /// <param name="report">The download's report.</param>
    /// <returns>"x downloaded, y updated, z failed" and one line per failure and store problem.</returns>
    internal static (string Status, List<string> Notices) Summarize(DownloadReport report)
    {
        int updated = report.Updated.Count + report.ServerChanged.Count;
        string status = $"{report.Downloaded.Count} downloaded, {updated} updated, {report.Failed.Count} failed";
        return (
            status,
            [
                .. report.Failed.Select(f => $"{(string.IsNullOrEmpty(f.Name) ? f.Id : f.Name)}: {f.Reason}"),
                .. report.Problems.Select(p => $"{p.FileName}: {p.Reason}"),
            ]
        );
    }

    private bool CanRefresh() => (_artcc is not null) && !IsResyncRunning;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => StartResync();

    // On the UI thread.
    private Task StartResync()
    {
        string artcc = _artcc!;
        int seq = Interlocked.Increment(ref _resyncSeq);
        IsResyncRunning = true;
        ProgressDone = 0;
        ProgressTotal = 0;
        StatusText = "Downloading…";
        _eventNotices.Clear();
        RebuildNotices();
        ResyncTask = ResyncAsync(artcc, seq, _runCts.Token);
        return ResyncTask;
    }

    private async Task ResyncAsync(string artcc, int seq, CancellationToken ct)
    {
        var progress = new PostedProgress(_services.Post, p => ShowProgress(seq, p));
        (DownloadReport? report, string? failure) = await DownloadAsync(artcc, progress, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !IsCurrentResync(seq))
        {
            return;
        }

        await ReloadAsync(artcc, null).ConfigureAwait(false);
        await OnUiAsync(() => FinishResync(seq, report, failure)).ConfigureAwait(false);
    }

    // The report, or why the download did not run (a throw counts as the list call failing); both null when cancelled.
    // The download starts on a pool thread, so none of its continuations run on the UI thread.
    private async Task<(DownloadReport? Report, string? Failure)> DownloadAsync(string artcc, PostedProgress progress, CancellationToken ct)
    {
        try
        {
            return (await Task.Run(() => _services.Download(artcc, progress, ct), ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "downloading the server scenarios of " + artcc);
            return (null, ex.Message);
        }
    }

    private void ShowProgress(int seq, DownloadProgress progress)
    {
        if (!IsCurrentResync(seq))
        {
            return;
        }

        ProgressDone = progress.Completed;
        ProgressTotal = progress.Total;
        StatusText = $"Downloading {progress.Completed}/{progress.Total}…";
    }

    private void FinishResync(int seq, DownloadReport? report, string? failure)
    {
        if (!IsCurrentResync(seq))
        {
            return;
        }

        IsResyncRunning = false;
        if (report is { Offline: false })
        {
            (StatusText, List<string> notices) = Summarize(report);
            _eventNotices.AddRange(notices);
        }
        else
        {
            ShowOfflineStatus(OfflineCountText(ServerRows.Count));
            _eventNotices.Add($"Download failed: {report?.ListFailure ?? failure}");
        }

        RebuildNotices();
    }

    private string OfflineCountText(int count) =>
        count switch
        {
            0 => $"Offline: no downloaded scenarios for {_artcc}",
            1 => "Offline: 1 downloaded scenario",
            _ => $"Offline: {count} downloaded scenarios",
        };

    // Shows each progress report through the post seam.
    private sealed class PostedProgress(Action<Action> post, Action<DownloadProgress> show) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => post(() => show(value));
    }
}
