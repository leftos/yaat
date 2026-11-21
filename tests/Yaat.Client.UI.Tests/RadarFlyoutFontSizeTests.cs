using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Views;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.EuroScopeFlyouts;
using Yaat.Client.Views.Scope.Prefs;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests;

// The "Radar Tag Flyouts" font size (radarFlyoutFontSize, 8-24, default 12): it persists across a save and a load, and a
// Scope applies it to the EuroScope tag flyouts when it opens and at each change.
public class RadarFlyoutFontSizeTests
{
    private static FontSizePrefs WithFlyout(UserPreferences prefs, int radarFlyout) =>
        new(
            prefs.RadarDatablockFontSize,
            radarFlyout,
            prefs.GroundDatablockFontSize,
            prefs.GroundLabelFontSize,
            prefs.DataGridFontSize,
            prefs.TerminalFontSize,
            prefs.InterfaceFontSize
        );

    [Fact]
    public void RadarFlyoutFontSize_RoundTripsThroughTheFile_AndClamps()
    {
        var prefs = new UserPreferences();
        try
        {
            prefs.SetFontSizes(WithFlyout(prefs, 16));
            Assert.Equal(16, new UserPreferences().RadarFlyoutFontSize);

            prefs.SetFontSizes(WithFlyout(prefs, 99));
            Assert.Equal(24, new UserPreferences().RadarFlyoutFontSize);
        }
        finally
        {
            prefs.SetFontSizes(WithFlyout(prefs, 12));
        }
    }

    [AvaloniaFact]
    public async Task AScopeView_AppliesThePref_WhenItOpens_AndAtEachChange()
    {
        MainViewModel main = await MainAsync();
        UserPreferences prefs = main.Preferences;
        prefs.SetFontSizes(WithFlyout(prefs, 14));
        FlyoutAppearance.FontSize = 12;
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        var window = new Window { Content = new ScopeView { DataContext = scope } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(14, FlyoutAppearance.FontSize);

            prefs.SetFontSizes(WithFlyout(prefs, 18));

            Assert.Equal(18, FlyoutAppearance.FontSize);
            ContextMenu menu = EuroScopeAltitudeFlyout.Build(new AircraftModel { Callsign = "UAL238" }, new RecordingMenuHost(""));
            Setter size = Assert.Single(
                menu.Styles.OfType<Style>().SelectMany(s => s.Setters.OfType<Setter>()),
                s => s.Property == MenuItem.FontSizeProperty
            );
            Assert.Equal(18.0, size.Value);
        }
        finally
        {
            window.Close();
            scope.Dispose();
            prefs.SetFontSizes(WithFlyout(prefs, 12));
            FlyoutAppearance.FontSize = 12;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
