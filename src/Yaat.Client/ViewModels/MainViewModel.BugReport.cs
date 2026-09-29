using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Asks the user for the report's title, description, expectation and callsigns. The argument says
    /// whether a recording bundle will be attached (the user is in a room), so the prompt can say what
    /// is being sent. Set by the view; when it is null the command does nothing.
    /// </summary>
    public Func<bool, Task<BugReportForm?>>? BugReportPrompt { get; set; }

    /// <summary>
    /// Files a bug report: writes a bundle under <c>%LOCALAPPDATA%/yaat/bug-reports/</c> (the session
    /// recording, logs and bookmarks in a room, the client log alone otherwise), opens a prefilled
    /// GitHub issue in the browser, and reveals the bundle so it can be dragged into that issue —
    /// GitHub's new-issue URL cannot carry an attachment.
    /// </summary>
    [RelayCommand]
    private async Task FileBugReport()
    {
        if (BugReportPrompt is null || IsExportingRecording)
        {
            return;
        }

        BugReportForm? form = await BugReportPrompt(IsInRoom);
        if (form is null)
        {
            return;
        }

        string? bundlePath = null;
        string? attachmentName;
        try
        {
            string directory = YaatPaths.Combine("bug-reports");
            Directory.CreateDirectory(directory);

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            byte[]? recordingBytes = IsInRoom ? await FetchRecordingForBundleAsync() : null;

            attachmentName = recordingBytes is not null
                ? $"{timestamp}-{SanitizeFileName(ActiveScenarioName ?? "session")}.yaat-bug-report-bundle.zip"
                : $"{timestamp}-client-log.zip";

            bundlePath = Path.Combine(directory, attachmentName);
            await WriteBugReportBundleAsync(bundlePath, recordingBytes);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "File bug report bundle failed");
            DeletePartialBundle(bundlePath);
            attachmentName = null;
            bundlePath = null;
        }

        var env = new BugReportEnvironment(
            YaatVersion: BuildInfo.Version,
            BuildKind: BuildInfo.BuildKind,
            OperatingSystem: RuntimeInformation.OSDescription,
            ScenarioName: ActiveScenarioName,
            InRoom: IsInRoom
        );
        UrlLauncher.OpenInBrowser(BugReportIssueBuilder.BuildUrl(form, env, attachmentName));

        if (bundlePath is not null && File.Exists(bundlePath))
        {
            FileReveal.Show(bundlePath);
            StatusText = $"Bug report ready — drag {attachmentName} into the GitHub issue";
        }
        else
        {
            StatusText = "Bug report opened without a bundle — see the client log";
        }
    }

    /// <summary>
    /// Removes a bundle a failed write left half-written, so the bug-reports folder does not collect
    /// files that look like reports. Best effort: a file another process holds is logged, not surfaced.
    /// </summary>
    private void DeletePartialBundle(string? bundlePath)
    {
        if (bundlePath is null || !File.Exists(bundlePath))
        {
            return;
        }

        try
        {
            File.Delete(bundlePath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete the partial bug report bundle {Path}", bundlePath);
        }
    }
}
