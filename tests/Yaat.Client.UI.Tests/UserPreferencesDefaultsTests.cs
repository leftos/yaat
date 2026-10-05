using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests;

/// <summary>
/// <see cref="UserPreferences.CreateDefaults"/> holds the values a fresh preferences file starts with, whatever the
/// user's own file says, and never writes the file: a setter on it throws instead of saving.
/// </summary>
public class UserPreferencesDefaultsTests
{
    [Fact]
    public void CreateDefaults_ReadsTheFreshFileDefaults_NotTheUsersFile()
    {
        using var scope = new PreferencesFileScope();
        var user = new UserPreferences();
        user.SetAutoAcceptSettings(enabled: false, delaySeconds: 9);
        user.SetTakeControlKey("Ctrl+Y");
        user.SetPilotVoiceSettings(enabled: true, volume: 30, radioFxEnabled: false, speechRate: 1.3);

        var defaults = UserPreferences.CreateDefaults();

        Assert.True(defaults.AutoAcceptEnabled);
        Assert.Equal(5, defaults.AutoAcceptDelaySeconds);
        Assert.Equal("Ctrl+T", defaults.TakeControlKey);
        Assert.False(defaults.PilotVoiceEnabled);
        Assert.Equal(80, defaults.PilotVoiceVolume);
        Assert.True(defaults.PilotVoiceRadioFxEnabled);
        Assert.Equal(GroundColorScheme.Default, defaults.GroundColors);
        Assert.Equal(TerminalColorScheme.Default, defaults.TerminalColors);
        Assert.Equal(12, defaults.TerminalFontSize);
        Assert.Empty(defaults.Macros);
        Assert.Equal(
            CommandScheme.Default().Patterns[CanonicalCommandType.FlyHeading].Aliases,
            defaults.CommandScheme.Patterns[CanonicalCommandType.FlyHeading].Aliases
        );
    }

    [Fact]
    public void CreateDefaults_HoldsThePopOutAndBarHotkeys()
    {
        var defaults = UserPreferences.CreateDefaults();

        Assert.Equal("Ctrl+Shift+L", defaults.PopOutAircraftListKey);
        Assert.Equal("Ctrl+Shift+G", defaults.PopOutGroundViewKey);
        Assert.Equal("Ctrl+Shift+R", defaults.PopOutRadarViewKey);
        Assert.Equal("Ctrl+Shift+E", defaults.PopOutTerminalKey);
        Assert.Equal("Ctrl+Shift+C", defaults.PopOutControllersKey);
        Assert.Equal("Ctrl+Shift+M", defaults.PopOutMetarKey);
        Assert.Equal("Ctrl+Shift+F", defaults.FavoritesBarKey);
        Assert.Equal("Ctrl+Shift+T", defaults.AlwaysOnTopKey);
    }

    [Fact]
    public void ASetterOnTheDefaults_Throws_AndLeavesTheUsersFileAlone()
    {
        using var scope = new PreferencesFileScope();
        new UserPreferences().SetAutoAcceptSettings(enabled: true, delaySeconds: 9);
        var defaults = UserPreferences.CreateDefaults();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => defaults.SetAutoAcceptSettings(enabled: true, delaySeconds: 3));

        Assert.Contains("a defaults instance is read-only", ex.Message, StringComparison.Ordinal);
        Assert.Equal(9, new UserPreferences().AutoAcceptDelaySeconds);
    }
}
