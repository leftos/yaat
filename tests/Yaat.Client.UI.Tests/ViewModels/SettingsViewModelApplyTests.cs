using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Apply commits the Settings window's edits without closing it, as often as the user presses it: each commit
/// raises <see cref="SettingsViewModel.Applied"/>, an edit made after it stays out of the preferences until the
/// next one, and a commit writes a window's always-on-top setting only when it changed since the last one.
/// </summary>
public class SettingsViewModelApplyTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void ApplyThenALaterEdit_KeepsTheAppliedValue_AndLeavesTheLaterEditUncommitted()
    {
        using var scope = new PreferencesFileScope();
        int original = new UserPreferences().TerminalFontSize;
        var vm = new SettingsViewModel();
        int applied = 0;
        vm.Applied += () => applied++;

        vm.TerminalFontSize = original + 2;
        vm.ApplyCommand.Execute(null);
        vm.TerminalFontSize = original + 4;

        Assert.Equal(1, applied);
        Assert.Equal(original + 2, new UserPreferences().TerminalFontSize);

        vm.ApplyCommand.Execute(null);

        Assert.Equal(2, applied);
        Assert.Equal(original + 4, new UserPreferences().TerminalFontSize);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_PersistsBothSoloPacingDefaults()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel { SoloParkingInitialCallupIntervalSeconds = 40, SoloArrivalGeneratorRatePercent = 35 };
        vm.ApplyCommand.Execute(null);

        var stored = new UserPreferences();
        Assert.Equal(50, stored.SoloParkingInitialCallupRatePercent);
        Assert.Equal(35, stored.SoloArrivalGeneratorRatePercent);
        Assert.Equal("Once per 40 sec", vm.SoloParkingInitialCallupIntervalLabel);
    }

    /// <summary>
    /// A stored call-up rate the interval slider cannot show exactly (150% sits between 10 s and 20 s) survives an Apply
    /// that did not touch the slider, instead of snapping to the slider's nearest value.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_LeavesAnUntouchedOffGridPacingDefaultAsStored()
    {
        using var scope = new PreferencesFileScope();
        new UserPreferences().SetSoloPacingRates(150, 35);
        var vm = new SettingsViewModel();

        vm.ApplyCommand.Execute(null);

        var stored = new UserPreferences();
        Assert.Equal(150, stored.SoloParkingInitialCallupRatePercent);
        Assert.Equal(35, stored.SoloArrivalGeneratorRatePercent);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_WritesOnlyTheAlwaysOnTopSettingsThatChanged()
    {
        using var scope = new PreferencesFileScope();
        var preferences = new UserPreferences();
        var fired = new List<(string Window, bool IsTopmost)>();
        preferences.WindowTopmostChanged += (window, isTopmost) => fired.Add((window, isTopmost));
        var vm = new SettingsViewModel(preferences);

        vm.ApplyCommand.Execute(null);

        Assert.Empty(fired);

        bool terminalTopmost = !vm.TerminalTopmost;
        vm.TerminalTopmost = terminalTopmost;
        vm.ApplyCommand.Execute(null);

        Assert.Equal([("Terminal", terminalTopmost)], fired);

        vm.ApplyCommand.Execute(null);

        Assert.Single(fired);
    }
}
