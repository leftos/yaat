using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    private static KeybindRow Row(SettingsViewModel vm, string id) => vm.KeybindRows.Single(r => r.Id == id);

    private static void Capture(SettingsViewModel vm, string id, Key key, KeyModifiers modifiers)
    {
        Row(vm, id).StartCaptureCommand.Execute(null);
        vm.CaptureKey(key, modifiers);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void AClashBetweenTwoRows_BlocksApply_AndNamesTheOtherActionOnBoth()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        int applied = 0;
        vm.Applied += () => applied++;

        Capture(vm, "TakeControl", Key.L, KeyModifiers.Control | KeyModifiers.Shift);

        // The combo is accepted into the row, so the user sees what they pressed.
        Assert.Equal("Ctrl+Shift+L", Row(vm, "TakeControl").Combo);
        Assert.True(vm.HasKeybindClash);
        Assert.Equal("Also used by Pop out aircraft list", Row(vm, "TakeControl").ClashMessage);
        Assert.Equal("Also used by Take control", Row(vm, "PopOutAircraftList").ClashMessage);
        Assert.Equal("Key clash: Take control and Pop out aircraft list (Keys)", vm.KeybindClashSummary);
        Assert.Null(Row(vm, "QuickBookmark").ClashMessage);
        Assert.Equal(0, applied);
    }

    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData(Key.M, "Also used by Measure tool")]
    [InlineData(Key.F8, "Also used by Toggle DCB")]
    [InlineData(Key.F, "Also used by Strips and vTDLS find")]
    [InlineData(Key.D, "Also used by Ground view debug overlay")]
    public void AClashWithAFixedChord_BlocksApply_AndNamesTheFixedAction(Key key, string message)
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();

        Capture(vm, "QuickBookmark", key, KeyModifiers.Control);

        Assert.True(vm.HasKeybindClash);
        Assert.Equal(message, Row(vm, "QuickBookmark").ClashMessage);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ResolvingTheClash_ReEnablesApply_AndClearsBothMessages()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Capture(vm, "TakeControl", Key.L, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.True(vm.HasKeybindClash);

        Capture(vm, "TakeControl", Key.Y, KeyModifiers.Control);

        Assert.False(vm.HasKeybindClash);
        Assert.Null(Row(vm, "TakeControl").ClashMessage);
        Assert.Null(Row(vm, "PopOutAircraftList").ClashMessage);

        vm.ApplyCommand.Execute(null);

        Assert.Equal("Ctrl+Y", new UserPreferences().TakeControlKey);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ABareLetter_IsRefusedForAWindowHotkey_ButAFunctionKeyIsAccepted()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        string before = Row(vm, "PopOutRadarView").Combo;

        Capture(vm, "PopOutRadarView", Key.K, KeyModifiers.None);

        Assert.Equal(before, Row(vm, "PopOutRadarView").Combo);
        Assert.Equal("Add Ctrl or Alt, or use an F-key", Row(vm, "PopOutRadarView").CaptureHint);
        // Still listening, so the next press can carry the modifier.
        Assert.True(vm.IsCapturingKey);

        vm.CaptureKey(Key.F6, KeyModifiers.None);

        Assert.Equal("F6", Row(vm, "PopOutRadarView").Combo);
        Assert.Null(Row(vm, "PopOutRadarView").CaptureHint);
        Assert.False(vm.IsCapturingKey);

        // A bare Escape cannot be a window hotkey, so it backs out of the capture and keeps the combo.
        Capture(vm, "PopOutRadarView", Key.Escape, KeyModifiers.None);

        Assert.False(vm.IsCapturingKey);
        Assert.Equal("F6", Row(vm, "PopOutRadarView").Combo);
        Assert.Equal("F6", Row(vm, "PopOutRadarView").Display);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void FocusCommandInput_AcceptsABareKey()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();

        Capture(vm, "FocusInput", Key.F, KeyModifiers.None);

        Assert.Equal("F", Row(vm, "FocusInput").Combo);
        Assert.False(vm.IsCapturingKey);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void APushToTalkClash_ShowsOnThePttRow_AndNamesTheSpeechSection()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();

        vm.StartPttKeyCaptureCommand.Execute(null);
        vm.CaptureKey(Key.T, KeyModifiers.Control);

        Assert.True(vm.HasKeybindClash);
        Assert.Equal("Also used by Take control", vm.PttKeyClashMessage);
        Assert.Equal("Key clash: Take control (Keys) and Push-to-talk (Speech)", vm.KeybindClashSummary);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_WritesEveryKeybind_AndANewPreferencesReadsThemBack()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        Key[] functionKeys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F9, Key.F10, Key.F11, Key.F12, Key.F13, Key.F14];
        Assert.Equal(functionKeys.Length, vm.KeybindRows.Count);
        foreach ((KeybindRow row, Key key) in vm.KeybindRows.Zip(functionKeys))
        {
            Capture(vm, row.Id, key, KeyModifiers.Control | KeyModifiers.Alt);
        }
        Assert.False(vm.HasKeybindClash);

        vm.ApplyCommand.Execute(null);

        var stored = new UserPreferences();
        string[] expected = [.. functionKeys.Select(k => $"Ctrl+Alt+{k}")];
        string[] actual =
        [
            stored.AircraftSelectKey,
            stored.FocusInputKey,
            stored.TakeControlKey,
            stored.AlwaysOnTopKey,
            stored.QuickBookmarkKey,
            stored.PopOutAircraftListKey,
            stored.PopOutGroundViewKey,
            stored.PopOutRadarViewKey,
            stored.PopOutTerminalKey,
            stored.PopOutControllersKey,
            stored.PopOutMetarKey,
            stored.FavoritesBarKey,
            stored.PttKey,
        ];
        Assert.Equal(expected, actual);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ABareLetter_IsAcceptedForPushToTalk()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();

        vm.StartPttKeyCaptureCommand.Execute(null);
        vm.CaptureKey(Key.K, KeyModifiers.None);

        Assert.Equal("K", Row(vm, "Ptt").Combo);
        Assert.Equal("K", vm.PttKeyDisplay);
        Assert.False(vm.IsCapturingKey);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ResettingTheKeysSection_RestoresEveryRowToItsDefault()
    {
        using var scope = new PreferencesFileScope();
        var vm = new SettingsViewModel();
        var expected = new Dictionary<string, string>
        {
            ["AircraftSelect"] = "Add",
            ["FocusInput"] = "OemTilde",
            ["TakeControl"] = "Ctrl+T",
            ["AlwaysOnTop"] = "Ctrl+Shift+T",
            ["QuickBookmark"] = "Ctrl+B",
            ["PopOutAircraftList"] = "Ctrl+Shift+L",
            ["PopOutGroundView"] = "Ctrl+Shift+G",
            ["PopOutRadarView"] = "Ctrl+Shift+R",
            ["PopOutTerminal"] = "Ctrl+Shift+E",
            ["PopOutControllers"] = "Ctrl+Shift+C",
            ["PopOutMetar"] = "Ctrl+Shift+M",
            ["FavoritesBar"] = "Ctrl+Shift+F",
        };
        Assert.Equal(expected.Keys, vm.KeysSectionKeybindRows.Select(r => r.Id));

        Key[] functionKeys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F9, Key.F10, Key.F11, Key.F12, Key.F13];
        foreach ((KeybindRow row, Key key) in vm.KeysSectionKeybindRows.Zip(functionKeys))
        {
            Capture(vm, row.Id, key, KeyModifiers.Alt);
        }
        Assert.All(vm.KeysSectionKeybindRows, r => Assert.NotEqual(expected[r.Id], r.Combo));

        vm.SelectedSection = SettingsSectionId.Keys;
        vm.ResetSectionCommand.Execute(null);

        Assert.All(vm.KeysSectionKeybindRows, r => Assert.Equal(expected[r.Id], r.Combo));
        Assert.All(vm.KeysSectionKeybindRows, r => Assert.Equal(SettingsViewModel.KeyComboToDisplay(expected[r.Id]), r.Display));
        Assert.False(vm.HasKeybindClash);
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
