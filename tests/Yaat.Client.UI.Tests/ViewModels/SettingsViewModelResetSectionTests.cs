using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Reset section puts every setting the selected section shows back to the fresh-file default as a pending edit:
/// the preferences keep the old value until Apply, and Cancel discards the reset. Link-only sections have nothing
/// to reset, so the command is disabled there.
/// </summary>
public class SettingsViewModelResetSectionTests
{
    private const CanonicalCommandType VerbUnderTest = CanonicalCommandType.FlyHeading;

    [AvaloniaFact(Timeout = 60_000)]
    public void General_ResetsDiscordAndWindows() =>
        AssertResetsToDefault(
            SettingsSectionId.General,
            vm =>
            {
                vm.DiscordRichPresenceEnabled = false;
                vm.RaiseWindowsTogether = false;
                vm.MainWindowTopmost = true;
            },
            vm => (vm.DiscordRichPresenceEnabled, vm.RaiseWindowsTogether, vm.MainWindowTopmost),
            p => (p.DiscordRichPresenceEnabled, p.RaiseWindowsTogether, p.MainWindowGeometry?.IsTopmost ?? false)
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void General_KeepsTheInitials()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel { UserInitials = "QX" };
        vm.ApplyCommand.Execute(null);

        vm.SelectedSection = SettingsSectionId.General;
        vm.ResetSectionCommand.Execute(null);
        vm.ApplyCommand.Execute(null);

        Assert.Equal("QX", vm.UserInitials);
        Assert.Equal("QX", new UserPreferences().UserInitials);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Appearance_ResetsFontSizesZoomsAndRenderer() =>
        AssertResetsToDefault(
            SettingsSectionId.Appearance,
            vm =>
            {
                vm.TerminalFontSize = 16;
                vm.StripsZoomPercent = 120;
                vm.SelectedRendererModeIndex = 3;
            },
            vm => (vm.TerminalFontSize, vm.StripsZoomPercent, vm.SelectedRendererModeIndex),
            p => (p.TerminalFontSize, p.StripsZoomPercent, (int)p.RendererMode)
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void ScenarioDefaults_ResetsItsSettings() =>
        AssertResetsToDefault(
            SettingsSectionId.ScenarioDefaults,
            vm =>
            {
                vm.AutoAcceptDelaySeconds = 9;
                vm.AutoCrossRunway = true;
                vm.SelectedAutoDeleteIndex = 2;
                vm.SoloParkingInitialCallupIntervalSeconds = 40;
                vm.SoloArrivalGeneratorRatePercent = 35;
            },
            vm =>
                (
                    vm.AutoAcceptDelaySeconds,
                    vm.AutoCrossRunway,
                    SettingsViewModel.IndexToAutoDeleteOverride(vm.SelectedAutoDeleteIndex),
                    SoloPacing.ParkingInitialCallupIntervalSecondsToRate(vm.SoloParkingInitialCallupIntervalSeconds),
                    vm.SoloArrivalGeneratorRatePercent
                ),
            p =>
                (
                    p.AutoAcceptDelaySeconds,
                    p.AutoCrossRunway,
                    p.AutoDeleteOverride,
                    p.SoloParkingInitialCallupRatePercent,
                    p.SoloArrivalGeneratorRatePercent
                )
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void Radar_ResetsItsSettingsAndColours() =>
        AssertResetsToDefault(
            SettingsSectionId.Radar,
            vm =>
            {
                vm.TpaConeHalfAngleDegrees = 6.0;
                vm.SelectedColor = "#123456";
                vm.AssignmentTintEnabled = true;
            },
            vm => (vm.TpaConeHalfAngleDegrees, vm.SelectedColor, vm.AssignmentTintEnabled),
            p => (p.TpaConeHalfAngleDegrees, p.SelectedColor, p.AssignmentTintEnabled)
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void Ground_ResetsItsSettingsAndColours() =>
        AssertResetsToDefault(
            SettingsSectionId.Ground,
            vm =>
            {
                vm.GroundBackgroundColor = "#123456";
                vm.GroundSatelliteImageBrightness = 10;
                vm.GroundShowAllTaxiRoutes = true;
            },
            vm => (vm.GroundBackgroundColor, vm.GroundSatelliteImageBrightness, vm.GroundShowAllTaxiRoutes),
            p => (p.GroundColors.Background, p.GroundSatelliteImageBrightness, p.GroundShowAllTaxiRoutes)
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void Terminal_ResetsItsColours() =>
        AssertResetsToDefault(
            SettingsSectionId.Terminal,
            vm => vm.TerminalSayColor = "#123456",
            vm => vm.TerminalSayColor,
            p => p.TerminalColors.Say
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void CommandInput_ResetsItsSettings() =>
        AssertResetsToDefault(
            SettingsSectionId.CommandInput,
            vm =>
            {
                vm.AutoExpandSuggestionOnEnter = false;
                vm.SelectedSignatureHelpPlacementIndex = 1;
            },
            vm => (vm.AutoExpandSuggestionOnEnter, vm.SelectedSignatureHelpPlacementIndex),
            p => (p.AutoExpandSuggestionOnEnter, p.SignatureHelpPlacement == "Below" ? 1 : 0)
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void CommandVerbs_ResetsEveryVerb() =>
        AssertResetsToDefault(
            SettingsSectionId.CommandVerbs,
            vm => vm.VerbMappings.First(r => r.CommandType == VerbUnderTest).Aliases = "ZZTEST",
            vm => vm.VerbMappings.First(r => r.CommandType == VerbUnderTest).Aliases,
            p => string.Join(", ", p.CommandScheme.Patterns[VerbUnderTest].Aliases)
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void Macros_ClearsEveryMacro_AndTheAliasFolder() =>
        AssertResetsToDefault(
            SettingsSectionId.Macros,
            vm =>
            {
                vm.MacroRows.Add(new MacroRow { Name = "TST", Expansion = "CM 50" });
                vm.CrcAliasDirectory = "C:/aliases";
            },
            vm => (vm.MacroRows.Count, vm.CrcAliasDirectory),
            p => (p.Macros.Count, p.CrcAliasDirectory ?? "")
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void Keys_ResetsTheKeys() =>
        AssertResetsToDefault(
            SettingsSectionId.Keys,
            vm =>
            {
                vm.KeybindRows.Single(r => r.Id == "TakeControl").StartCaptureCommand.Execute(null);
                vm.CaptureKey(Key.Y, KeyModifiers.Control);
                vm.KeybindRows.Single(r => r.Id == "PopOutMetar").StartCaptureCommand.Execute(null);
                vm.CaptureKey(Key.J, KeyModifiers.Control);
            },
            vm => (vm.KeybindRows.Single(r => r.Id == "TakeControl").Display, vm.KeybindRows.Single(r => r.Id == "PopOutMetar").Display),
            p => (SettingsViewModel.KeyComboToDisplay(p.TakeControlKey), SettingsViewModel.KeyComboToDisplay(p.PopOutMetarKey))
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void Speech_ResetsTheSettings() =>
        AssertResetsToDefault(
            SettingsSectionId.Speech,
            vm =>
            {
                vm.PilotVoiceVolume = 30;
                vm.LlmGpuLayers = 10;
                vm.AutoFocusInputAfterSpeech = false;
            },
            vm => (vm.PilotVoiceVolume, vm.LlmGpuLayers, vm.AutoFocusInputAfterSpeech),
            p => (p.PilotVoiceVolume, p.LlmGpuLayers, p.AutoFocusInputAfterSpeech)
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void AudioDevices_ResetsToTheSystemDefault() =>
        AssertResetsToDefault(
            SettingsSectionId.AudioDevices,
            vm => vm.AudioInputDevice = "Test microphone",
            vm => vm.AudioInputDevice,
            p => p.AudioInputDevice
        );

    [AvaloniaFact(Timeout = 60_000)]
    public void ServerAdmin_TurnsAdminModeOff_AndEmptiesThePassword() =>
        AssertResetsToDefault(
            SettingsSectionId.ServerAdmin,
            vm =>
            {
                vm.IsAdminMode = true;
                vm.AdminPassword = "secret";
            },
            vm => (vm.IsAdminMode, vm.AdminPassword),
            p => (p.IsAdminMode, p.AdminPassword)
        );

    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData(SettingsSectionId.AircraftList)]
    [InlineData(SettingsSectionId.StripsAndTdls)]
    public void LinkOnlySections_DisableReset(SettingsSectionId section)
    {
        var vm = new SettingsViewModel(UserPreferences.CreateDefaults()) { SelectedSection = section };

        Assert.False(vm.ResetSectionCommand.CanExecute(null));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void TheFooterButton_FollowsTheSelectedSection()
    {
        var window = new SettingsWindow();
        window.ShowAndRunLayout();
        try
        {
            Button reset = window.FindControl<Button>("ResetSectionButton")!;
            Assert.True(reset.IsEffectivelyEnabled);

            window.SelectSection(SettingsSectionId.AircraftList);
            Dispatcher.UIThread.RunJobs();

            Assert.False(reset.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CancelAfterReset_LeavesThePreferencesUnchanged()
    {
        using var scope = new PreferencesFileScope();
        new UserPreferences().SetPilotVoiceSettings(enabled: true, volume: 30, radioFxEnabled: false, speechRate: 1.3);
        var window = new SettingsWindow(
            new UserPreferences(),
            audioCapture: null,
            speechSampleStore: null,
            new FavoriteStore(FavoriteStore.DefaultRootDir)
        );
        window.ShowAndRunLayout();
        var vm = (SettingsViewModel)window.DataContext!;

        window.SelectSection(SettingsSectionId.Speech);
        Dispatcher.UIThread.RunJobs();
        vm.ResetSectionCommand.Execute(null);
        window.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
        Assert.Equal(30, new UserPreferences().PilotVoiceVolume);
    }

    // Edits the section away from its defaults and commits that, then checks Reset section shows the defaults while the
    // preferences keep the edit, and that Apply then commits the defaults.
    private static void AssertResetsToDefault<T>(
        SettingsSectionId section,
        Action<SettingsViewModel> edit,
        Func<SettingsViewModel, T> shown,
        Func<UserPreferences, T> stored
    )
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        edit(vm);
        vm.ApplyCommand.Execute(null);
        T edited = stored(new UserPreferences());
        T defaults = stored(UserPreferences.CreateDefaults());
        Assert.NotEqual(defaults, edited);

        vm.SelectedSection = section;
        vm.ResetSectionCommand.Execute(null);

        Assert.Equal(defaults, shown(vm));
        Assert.Equal(edited, stored(new UserPreferences()));

        vm.ApplyCommand.Execute(null);

        Assert.Equal(defaults, stored(new UserPreferences()));
    }
}
