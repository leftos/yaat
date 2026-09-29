using Microsoft.Extensions.Logging;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The one-time offer to send push-to-talk recordings to the YAAT developers. Turning speech-to-text
/// on raises the offer, and a user who already had it on before the feature shipped is offered once
/// when the main window opens. Accepting persists the opt-in and uploads whatever is already
/// pending; declining only records that the offer was shown — it never turns speech-to-text off, and
/// the checkbox in Settings → Speech changes the answer later.
/// </summary>
public partial class MainViewModel
{
    private bool _speechTelemetryPromptOpen;

    /// <summary>
    /// Set by the owning view to show the telemetry opt-in dialog and return the user's answer. Null
    /// (a headless host) means never prompt.
    /// </summary>
    public Func<Task<bool>>? SpeechTelemetryPrompt { get; set; }

    /// <summary>
    /// Offers the opt-in when speech-to-text is on and the offer has not been shown yet. Returns at
    /// once otherwise, and while a prompt is already on screen, so the two triggers (the first enable
    /// and the window opening) never stack two dialogs. Both triggers fire and forget, so a failure is
    /// logged here and leaves the offer due.
    /// </summary>
    public async Task OfferSpeechTelemetryIfDueAsync()
    {
        if (_speechTelemetryPromptOpen || SpeechTelemetryPrompt is null || !_preferences.SpeechEnabled || _preferences.SpeechTelemetryPromptShown)
        {
            return;
        }

        _speechTelemetryPromptOpen = true;
        try
        {
            bool accepted = await SpeechTelemetryPrompt();
            _preferences.SetSpeechTelemetryEnabled(accepted);
            _preferences.SetSpeechTelemetryPromptShown(true);

            if (accepted)
            {
                await UploadSpeechTelemetryAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Speech telemetry offer failed");
        }
        finally
        {
            _speechTelemetryPromptOpen = false;
        }
    }

    /// <summary>
    /// Uploads every sample still marked pending, against the currently connected server, on a
    /// thread-pool thread: the pass reads and zips sample files before its first await. A failure is
    /// logged and dropped: the samples stay pending for the next session, which is better than
    /// surfacing a network error to a user who never asked for the upload.
    /// </summary>
    private async Task UploadSpeechTelemetryAsync()
    {
        string url = _connectedServerUrl;
        try
        {
            await Task.Run(() => _speechTelemetryUploader.UploadPendingAsync(url, () => _auth.GetValidAccessTokenAsync(url), CancellationToken.None));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Speech telemetry upload failed");
        }
    }
}
