using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yaat.Client.ViewModels;

/// <summary>What the student chose in the "Pilot voice is off" dialog shown before a solo session resumes.</summary>
public enum PilotVoiceWarningChoice
{
    OpenVoiceSettings,
    StartAnyway,
    Cancel,
}

/// <summary>
/// Warns a solo-training student whose pilot voice (TTS) is not working. Solo pilots speak only through TTS, so a
/// student without it hears no readbacks or requests. <see cref="NoPilotVoiceInSolo"/> drives a banner while the
/// condition holds; the first resume of each session is intercepted by a dialog until the student chooses
/// "Start anyway". A scenario load, a recording load or a room change starts a new session; a reconnect that re-applies
/// the same room does not.
/// </summary>
public partial class MainViewModel
{
    /// <summary>True while the room runs a solo scenario and pilot voice is off or unavailable.</summary>
    [ObservableProperty]
    private bool _noPilotVoiceInSolo;

    private bool _pilotVoiceWarningAcknowledged;

    /// <summary>
    /// Set by the owning view to show the "Pilot voice is off" dialog and return the student's choice. Null (a headless
    /// host) lets every resume through.
    /// </summary>
    public Func<Task<PilotVoiceWarningChoice>>? PilotVoiceWarningPrompt { get; set; }

    /// <summary>Raised when the student asks for the pilot voice settings, from the banner or the dialog.</summary>
    public event Action? PilotVoiceSettingsRequested;

    /// <summary>Re-evaluates <see cref="NoPilotVoiceInSolo"/> from the room, the scenario, the preference and the voice pack.</summary>
    public void RefreshPilotVoiceWarning()
    {
        bool soloSession = IsInRoom && HasScenario && SessionSoloTrainingMode;

        // Short-circuits so the voice-pack and audio-device probe runs only for a solo session.
        EvaluatePilotVoiceWarning(soloSession, soloSession && _preferences.PilotVoiceEnabled && _pilotVoice.IsAvailable);
    }

    /// <summary>Sets <see cref="NoPilotVoiceInSolo"/> from its two inputs.</summary>
    public void EvaluatePilotVoiceWarning(bool soloSession, bool pilotVoiceUsable) => NoPilotVoiceInSolo = soloSession && !pilotVoiceUsable;

    /// <summary>Whether a resume should stop at the dialog: the warning holds and the student has not started anyway this session.</summary>
    public bool ShouldInterceptResume() => NoPilotVoiceInSolo && !_pilotVoiceWarningAcknowledged;

    /// <summary>
    /// Applies the student's dialog choice. Returns true when the resume goes ahead. "Start anyway" silences the dialog
    /// for the rest of the session; "Voice settings" opens the settings; neither that nor "Cancel" resumes.
    /// </summary>
    public bool ApplyPilotVoiceWarningChoice(PilotVoiceWarningChoice choice)
    {
        switch (choice)
        {
            case PilotVoiceWarningChoice.StartAnyway:
                _pilotVoiceWarningAcknowledged = true;
                return true;
            case PilotVoiceWarningChoice.OpenVoiceSettings:
                PilotVoiceSettingsRequested?.Invoke();
                return false;
            default:
                return false;
        }
    }

    [RelayCommand]
    private void OpenPilotVoiceSettings() => PilotVoiceSettingsRequested?.Invoke();

    /// <summary>Starts a new session for the once-per-session dialog.</summary>
    private void ResetPilotVoiceWarningSession() => _pilotVoiceWarningAcknowledged = false;

    /// <summary>
    /// Gate for every client path that resumes the sim. Returns true when the resume may be sent; otherwise the dialog
    /// was shown and the student did not choose "Start anyway".
    /// </summary>
    private async Task<bool> ConfirmResumeAsync()
    {
        RefreshPilotVoiceWarning();
        if (!ShouldInterceptResume() || PilotVoiceWarningPrompt is null)
        {
            return true;
        }

        PilotVoiceWarningChoice choice = await PilotVoiceWarningPrompt();
        return ApplyPilotVoiceWarningChoice(choice);
    }
}
