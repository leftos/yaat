using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Client.Services.Speech.Bench;

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

    /// <summary>Re-evaluates <see cref="NoPilotVoiceInSolo"/> from the room, the scenario, the preference and the voice pack.</summary>
    public void RefreshPilotVoiceWarning()
    {
        bool soloSession = IsInRoom && HasScenario && SessionSoloTrainingMode;

        // Short-circuits so the voice-pack and audio-device probe runs only for a solo session.
        EvaluatePilotVoiceWarning(soloSession, soloSession && _preferences.PilotVoiceEnabled && _pilotVoice.IsAvailable);
    }

    /// <summary>Sets <see cref="NoPilotVoiceInSolo"/> from its two inputs.</summary>
    public void EvaluatePilotVoiceWarning(bool soloSession, bool pilotVoiceUsable) => NoPilotVoiceInSolo = soloSession && !pilotVoiceUsable;

    /// <summary>
    /// Whether a voice will speak, so the voice should be loaded now: pilot voice is on, solo training is on by the preference or
    /// in the session, the pack is installed, and no speech benchmark is running. The load needs no audio device, so none is probed.
    /// </summary>
    public static bool ShouldWarmPilotVoice(bool pilotVoiceEnabled, bool soloPreference, bool sessionSolo, bool packFound, SttBenchState bench) =>
        pilotVoiceEnabled && (soloPreference || sessionSolo) && packFound && (bench == SttBenchState.Idle);

    /// <summary>
    /// Loads the voice on the thread pool when <see cref="ShouldWarmPilotVoice"/> holds. Safe to call repeatedly: a loaded voice
    /// loads nothing, and one already in memory stays there when the pack goes away or a benchmark starts.
    /// </summary>
    public void WarmPilotVoiceIfDue()
    {
        if (AppLifetime.IsShuttingDown)
        {
            return;
        }

        bool enabled = _preferences.PilotVoiceEnabled;
        bool soloPreference = _preferences.SoloTrainingMode;
        bool sessionSolo = SessionSoloTrainingMode;
        SttBenchState bench = SttBench.State;

        // Finding the pack walks the disk, so it runs off the UI thread and only once the other inputs allow a warm.
        if (!ShouldWarmPilotVoice(enabled, soloPreference, sessionSolo, packFound: true, bench))
        {
            return;
        }

        _ = Task.Run(async () =>
            {
                bool packFound = PilotVoicePack.FindInstalledDirectory() is not null;
                // The live state: a run may have started while the pack was being found.
                if (ShouldWarmPilotVoice(enabled, soloPreference, sessionSolo, packFound, SttBench.State))
                {
                    await _pilotVoice.WarmAsync(CancellationToken.None).ConfigureAwait(false);
                }
            })
            .ContinueWith(
                t => _log.LogError(t.Exception, "Warming the pilot voice failed"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default
            );
    }

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
                RequestSettings(SettingsSectionId.Speech);
                return false;
            default:
                return false;
        }
    }

    /// <summary>The banner's link: Settings at Speech, where pilot voice is turned on.</summary>
    [RelayCommand]
    private void OpenPilotVoiceSettings() => RequestSettings(SettingsSectionId.Speech);

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
