using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Settings → Speech "Speed" control on the solo pilot voice (0.75–1.5 in 0.05 steps, 1.1 default).
/// The preferences file is shared by every test in the process, so each test puts the rate back to the
/// 1.1 default when it is done.
/// </summary>
public class SettingsViewModelPilotVoiceSpeechRateTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void Load_ReadsSpeechRateFromPreferences()
    {
        var seed = new UserPreferences();
        seed.SetPilotVoiceSettings(false, 80, true, 1.35);

        try
        {
            var vm = new SettingsViewModel();
            Assert.Equal(1.35, vm.PilotVoiceSpeechRate);
        }
        finally
        {
            ResetRate();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_PersistsSpeechRate()
    {
        try
        {
            var vm = new SettingsViewModel { PilotVoiceSpeechRate = 1.3 };

            vm.ApplyCommand.Execute(null);

            Assert.Equal(1.3, new UserPreferences().PilotVoiceSpeechRate);
        }
        finally
        {
            ResetRate();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Default_IsOnePointOne()
    {
        ResetRate();

        var vm = new SettingsViewModel();
        Assert.Equal(1.1, vm.PilotVoiceSpeechRate);
    }

    private static void ResetRate()
    {
        var prefs = new UserPreferences();
        prefs.SetPilotVoiceSettings(prefs.PilotVoiceEnabled, prefs.PilotVoiceVolume, prefs.PilotVoiceRadioFxEnabled, 1.1);
    }
}
