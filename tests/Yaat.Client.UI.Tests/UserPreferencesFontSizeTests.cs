using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests;

// Covers the Terminal / Interface font sizes and the Strips / vTDLS page-zoom
// percents added for issue #178. UserPreferences writes to YaatPaths.AppDataRoot,
// redirected by ModuleInit to a per-process temp directory; a fresh instance reads
// preferences.json from disk and proves the round-trip. Each mutating test restores
// the default at the end so the Defaults test stays order-tolerant against the
// shared preferences.json.
public class UserPreferencesFontSizeTests
{
    private static FontSizePrefs DefaultFontSizes(int terminal, int @interface) =>
        new(RadarDatablock: 12, RadarFlyout: 12, GroundDatablock: 12, GroundLabel: 13, DataGrid: 12, Terminal: terminal, Interface: @interface);

    [Fact]
    public void NewFontPrefs_HaveExpectedDefaults()
    {
        var prefs = new UserPreferences();

        Assert.Equal(12, prefs.TerminalFontSize);
        Assert.Equal(12, prefs.InterfaceFontSize);
        Assert.Equal(80, prefs.StripsZoomPercent);
        Assert.Equal(100, prefs.TdlsZoomPercent);
    }

    [Fact]
    public void SetFontSizes_PersistsAndClampsTerminalAndInterface()
    {
        var prefs = new UserPreferences();

        prefs.SetFontSizes(DefaultFontSizes(terminal: 18, @interface: 16));
        Assert.Equal(18, prefs.TerminalFontSize);
        Assert.Equal(16, prefs.InterfaceFontSize);
        Assert.Equal(18, new UserPreferences().TerminalFontSize);
        Assert.Equal(16, new UserPreferences().InterfaceFontSize);

        // Out-of-range values clamp to [8, 24].
        prefs.SetFontSizes(DefaultFontSizes(terminal: 99, @interface: 1));
        Assert.Equal(24, prefs.TerminalFontSize);
        Assert.Equal(8, prefs.InterfaceFontSize);

        // Restore defaults so the Defaults test is order-independent.
        prefs.SetFontSizes(DefaultFontSizes(terminal: 12, @interface: 12));
        Assert.Equal(12, new UserPreferences().TerminalFontSize);
        Assert.Equal(12, new UserPreferences().InterfaceFontSize);
    }

    [Fact]
    public void SetStripsZoomPercent_PersistsAndClamps()
    {
        var prefs = new UserPreferences();

        prefs.SetStripsZoomPercent(150);
        Assert.Equal(150, prefs.StripsZoomPercent);
        Assert.Equal(150, new UserPreferences().StripsZoomPercent);

        prefs.SetStripsZoomPercent(999);
        Assert.Equal(200, prefs.StripsZoomPercent);
        prefs.SetStripsZoomPercent(10);
        Assert.Equal(50, prefs.StripsZoomPercent);

        prefs.SetStripsZoomPercent(80);
        Assert.Equal(80, new UserPreferences().StripsZoomPercent);
    }

    [Fact]
    public void SetTdlsZoomPercent_PersistsAndClamps()
    {
        var prefs = new UserPreferences();

        prefs.SetTdlsZoomPercent(130);
        Assert.Equal(130, prefs.TdlsZoomPercent);
        Assert.Equal(130, new UserPreferences().TdlsZoomPercent);

        prefs.SetTdlsZoomPercent(999);
        Assert.Equal(200, prefs.TdlsZoomPercent);
        prefs.SetTdlsZoomPercent(10);
        Assert.Equal(50, prefs.TdlsZoomPercent);

        prefs.SetTdlsZoomPercent(100);
        Assert.Equal(100, new UserPreferences().TdlsZoomPercent);
    }
}
