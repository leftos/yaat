using System.ComponentModel;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    /// <summary>
    /// Loads the recording through the same method as File &gt; Load Recording after its file pick, and answers once the client has
    /// applied it and rebuilt its terminal: the message is the status line, naming the scenario or the failure.
    /// </summary>
    [AutomationTool("load_recording", "Loads a recording file into the room and waits until the client shows it.", nameof(NotConnected))]
    public async Task<AppToolOutcome> LoadRecording(
        [Description("The recording's full path: a .yaat-recording.zip/.br/.json or a .yaat-bug-report-bundle.zip.")] string path
    )
    {
        if (!File.Exists(path))
        {
            throw new AppToolArgumentException("path", $"No recording file at '{path}'; give the full path of an existing file.");
        }

        return AppToolOutcome.Done(await _viewModel.LoadRecordingFromFileAsync(path));
    }
}
