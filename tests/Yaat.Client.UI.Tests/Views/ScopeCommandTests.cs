using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The <c>.scope</c> dot commands: <c>.scope range</c> and <c>.scope template</c> act on the open YAAT Scope and echo
/// themselves in the status line; the toolbar runs the same commands.
/// </summary>
public class ScopeCommandTests
{
    private const string Usage =
        "Usage: .scope range <1-256> | .scope template <stars|eram|mixed|euroscope> | .scope brite <group> <value>"
        + " | .scope map <id> <on|off> | .scope db <item> <value>"
        + " | .scope tearoff <button> <corner> <dx> <dy> | .scope tearoff delete <button>..."
        + " | .scope pref <save|saveas|load|rename|delete> | .scope profile <stars|eram|mixed>"
        + " | .scope others <full|partial> | .scope auto | .scope vector <0|1|2|4|8|own|all> | .scope hist <0-10>"
        + " | .scope rings <jring|cone> <size> <callsign> | .scope rings <dri5|dri3> <callsign> | .scope rings clear [callsign]"
        + " | .scope tdm <on|off> | .scope ptl <callsign> | .scope ptl clear [callsign]";

    private const string TearoffUsage = "Usage: .scope tearoff <button> <tl|tr|bl|br> <dx> <dy> | .scope tearoff delete <button>...";

    private const string BriteUsage =
        "Usage: .scope brite <group> <value>: mapa, mapb, targets, fdb, alerts, hist, vector, rings 0-100; line4 -20-0; dwell 0-20";

    [AvaloniaFact]
    public async Task Profile_SwitchesTheProfileAndItsTemplate_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope profile eram");

            Assert.Equal(ScopeProfileKind.Eram, main.ScopeProfile);
            Assert.Equal(ScopeProfileKind.Eram, scope.ProfileKind);
            Assert.Equal("eram", scope.TemplateId);
            Assert.Equal("Scope profile ERAM", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Others_PinsTheLevel_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope others FULL");

            Assert.Equal(ScopeProfile.OthersFull, scope.Pins.OthersLevel);
            Assert.Equal(ScopeProfile.OthersFull, scope.EffectiveBand.OthersLevel);
            Assert.Equal("Scope others FULL (pinned)", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Auto_ClearsThePins_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope others full");

            main.RunScopeCommand(".scope auto");

            Assert.Equal(ScopeBandPins.None, scope.Pins);
            Assert.Equal("Scope AUTO", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Auto_WithExtraWords_SaysTheUsage_AndClearsNothing()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope others full");

            main.RunScopeCommand(".scope auto now");

            Assert.Equal("Usage: .scope auto", main.StatusText);
            Assert.Equal(ScopeBandPins.None with { OthersLevel = ScopeProfile.OthersFull }, scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope others half")]
    [InlineData(".scope others")]
    [InlineData(".scope others full now")]
    public async Task Others_BadWordOrArity_SaysTheUsage_AndPinsNothing(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(command);

            Assert.Equal("Usage: .scope others <full|partial>", main.StatusText);
            Assert.Equal(ScopeBandPins.None, scope.Pins);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope profile wx", "Unknown scope profile 'wx'")]
    [InlineData(".scope profile", "Usage: .scope profile <stars|eram|mixed>")]
    public async Task Profile_BadIdOrArity_SaysSo_AndKeepsTheProfile(string command, string expected)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(command);

            Assert.Equal(expected, main.StatusText);
            Assert.Null(main.ScopeProfile);
            Assert.Equal(ScopeProfileKind.Mixed, scope.ProfileKind);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Range_SetsTheRange_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 25");

            Assert.Equal(25, scope.RangeNm);
            Assert.Equal(".scope range 25", main.StatusText);

            main.RunScopeCommand(".SCOPE RANGE 30");
            Assert.Equal(30, scope.RangeNm);
            Assert.Equal(".scope range 30", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Range_AcceptsItsBounds()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope range 1");
            Assert.Equal(1, scope.RangeNm);
            main.RunScopeCommand(".scope range 256");
            Assert.Equal(256, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("0")]
    [InlineData("257")]
    [InlineData("abc")]
    [InlineData("25.5")]
    public async Task Range_OutOfBoundsOrNotAWholeNumber_GivesTheUsage_AndLeavesTheRange(string value)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            double before = scope.RangeNm;

            main.RunScopeCommand($".scope range {value}");

            Assert.Equal("Usage: .scope range <1-256>", main.StatusText);
            Assert.Equal(before, scope.RangeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("eram", "eram")]
    [InlineData("ERAM", "eram")]
    [InlineData("Mixed", "mixed")]
    [InlineData("euroscope", "euroscope")]
    public async Task Template_PicksTheTemplate_AndEchoesTheCanonicalId(string typed, string expected)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            bool before = main.Preferences.EuroScopeMode;

            main.RunScopeCommand($".scope template {typed}");

            Assert.Equal(expected, scope.TemplateId);
            Assert.Equal($".scope template {expected}", main.StatusText);
            Assert.Equal(before, main.Preferences.EuroScopeMode);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Template_Unknown_SaysSo_AndLeavesTheTemplate()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            string before = scope.TemplateId;

            main.RunScopeCommand(".scope template radar");

            Assert.Equal("Unknown scope template: radar", main.StatusText);
            Assert.Equal(before, scope.TemplateId);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Brite_SetsTheGroup_AndEchoesInLowercase()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope brite fdb 85");
            Assert.Equal(85, scope.Brightness.Fdb);
            Assert.Equal(".scope brite fdb 85", main.StatusText);

            main.RunScopeCommand(".SCOPE BRITE DWELL 0");
            Assert.Equal(0, scope.Brightness.Dwell);
            Assert.Equal(".scope brite dwell 0", main.StatusText);

            main.RunScopeCommand(".scope brite line4 -20");
            Assert.Equal(-20, scope.Brightness.Line4);
            Assert.Equal(".scope brite line4 -20", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope brite fdb 101")]
    [InlineData(".scope brite line4 1")]
    [InlineData(".scope brite dwell 21")]
    [InlineData(".scope brite mapa -5")]
    [InlineData(".scope brite wx 50")]
    [InlineData(".scope brite fdb")]
    [InlineData(".scope brite")]
    [InlineData(".scope brite fdb 8.5")]
    [InlineData(".scope brite fdb 50 60")]
    public async Task Brite_OutOfRangeUnknownOrMalformed_GivesTheBriteUsage_AndLeavesTheState(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeBrightness before = scope.Brightness;

            main.RunScopeCommand(command);

            Assert.Equal(BriteUsage, main.StatusText);
            Assert.Equal(before, scope.Brightness);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope range 25")]
    [InlineData(".scope range abc")]
    [InlineData(".scope template eram")]
    [InlineData(".scope brite fdb 85")]
    [InlineData(".scope brite wx 50")]
    public async Task NoOpenScope_SaysSo(string command)
    {
        MainViewModel main = await MainAsync();

        main.RunScopeCommand(command);

        Assert.Equal("No YAAT Scope is open", main.StatusText);
    }

    [AvaloniaFact]
    public async Task ScopeAlone_OrAnotherVerb_GivesTheUsage()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope");
            Assert.Equal(Usage, main.StatusText);

            main.StatusText = "";
            main.RunScopeCommand(".scope zoom 5");
            Assert.Equal(Usage, main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    private const string MapUsage = "Usage: .scope map <id> <on|off> | .scope map clear";

    private const string DbUsage = "Usage: .scope db <4thline|dwell|atpa> <on|off> | .scope db char <8-24>";

    /// <summary>A Scope seeded from a centred radar with map toggles 2 and 10, every map off.</summary>
    private static async Task<(MainViewModel Main, ScopeViewModel Scope)> OpenScopeWithMapsAsync()
    {
        MainViewModel main = await MainAsync();
        RadarMapToggles.Add(main.Radar, 2, 10);
        main.Radar.CenterLat = 37.62;
        main.Radar.CenterLon = -122.38;
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        main.OpenScope = scope;
        return (main, scope);
    }

    [AvaloniaFact]
    public async Task Map_OnAndOff_SetsTheToggle_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeWithMapsAsync();
        try
        {
            main.RunScopeCommand(".scope map 10 on");
            Assert.Equal(".scope map 10 on", main.StatusText);
            Assert.Equal([false, true], scope.MapToggles.Select(t => t.IsOn));

            main.RunScopeCommand(".SCOPE MAP 10 OFF");
            Assert.Equal(".scope map 10 off", main.StatusText);
            Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
            Assert.All(main.Radar.MapToggles, t => Assert.False(t.IsEnabled));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Map_UnknownId_SaysSo()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeWithMapsAsync();
        try
        {
            main.RunScopeCommand(".scope map 99 on");

            Assert.Equal("Unknown video map: 99", main.StatusText);
            Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Map_Clear_TurnsEveryToggleOff_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeWithMapsAsync();
        try
        {
            main.RunScopeCommand(".scope map 2 on");
            main.RunScopeCommand(".scope map 10 on");

            main.RunScopeCommand(".scope map clear");

            Assert.Equal(".scope map clear", main.StatusText);
            Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope map 10")]
    [InlineData(".scope map 10 maybe")]
    [InlineData(".scope map")]
    [InlineData(".scope map 10 on off")]
    public async Task Map_WithoutAStateWordOrMalformed_GivesTheMapUsage(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeWithMapsAsync();
        try
        {
            main.RunScopeCommand(command);

            Assert.Equal(MapUsage, main.StatusText);
            Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope db atpa off", "atpa")]
    [InlineData(".scope db 4thline off", "4thline")]
    [InlineData(".SCOPE DB DWELL OFF", "dwell")]
    public async Task Db_ToggleOff_SetsItsSetting_AndEchoesInLowercase(string command, string item)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeDbSettings before = scope.DbSettings with { Atpa = true };
            scope.DbSettings = before;

            main.RunScopeCommand(command);

            Assert.Equal($".scope db {item} off", main.StatusText);
            ScopeDbSettings expected = item switch
            {
                "atpa" => before with { Atpa = false },
                "4thline" => before with { Line4 = false },
                _ => before with { HoverDwell = false },
            };
            Assert.Equal(expected, scope.DbSettings);

            main.RunScopeCommand($".scope db {item} on");
            Assert.Equal($".scope db {item} on", main.StatusText);
            Assert.Equal(before, scope.DbSettings);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Db_Char_SetsTheSize_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope db char 16");

            Assert.Equal(".scope db char 16", main.StatusText);
            Assert.Equal(16, scope.DbSettings.CharSize);

            main.RunScopeCommand(".scope db char 8");
            Assert.Equal(8, scope.DbSettings.CharSize);
            main.RunScopeCommand(".scope db char 24");
            Assert.Equal(24, scope.DbSettings.CharSize);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope db char 7")]
    [InlineData(".scope db char 25")]
    [InlineData(".scope db char x")]
    [InlineData(".scope db char")]
    [InlineData(".scope db atpa maybe")]
    [InlineData(".scope db atpa")]
    [InlineData(".scope db")]
    public async Task Db_BadValueOrArity_GivesTheDbUsage_AndLeavesTheSettings(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeDbSettings before = scope.DbSettings;

            main.RunScopeCommand(command);

            Assert.Equal(DbUsage, main.StatusText);
            Assert.Equal(before, scope.DbSettings);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope db ldr 2", "ldr")]
    [InlineData(".scope db ldrdir 9", "ldrdir")]
    [InlineData(".scope db cwt on", "cwt")]
    [InlineData(".scope db reqalt on", "reqalt")]
    [InlineData(".scope db portal on", "portal")]
    [InlineData(".scope db wx on", "wx")]
    public async Task Db_UnbuiltOrUnknownItem_SaysSo(string command, string item)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeDbSettings before = scope.DbSettings;

            main.RunScopeCommand(command);

            Assert.Equal($"Unknown scope DB item: {item}", main.StatusText);
            Assert.Equal(before, scope.DbSettings);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope map 10 on")]
    [InlineData(".scope map clear")]
    [InlineData(".scope db char 16")]
    [InlineData(".scope db ldr 2")]
    public async Task NoOpenScope_MapAndDb_SaySo(string command)
    {
        MainViewModel main = await MainAsync();

        main.RunScopeCommand(command);

        Assert.Equal("No YAAT Scope is open", main.StatusText);
    }

    private static ScopeTearoff Tearoff(ScopeButtonId id, ScopeCorner corner, int dx, int dy) =>
        new(new ScopeButtonKey(id, null), new ScopePlacement(corner, dx, dy));

    [AvaloniaFact]
    public async Task Tearoff_Place_EchoesTheCanonicalText_AndAddsOneEntry()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeWithMapsAsync();
        try
        {
            main.RunScopeCommand(".SCOPE TEAROFF Range TR 12 34");

            Assert.Equal(".scope tearoff range tr 12 34", main.StatusText);
            Assert.Equal([Tearoff(ScopeButtonId.Range, ScopeCorner.TopRight, 12, 34)], scope.Tearoffs);

            main.RunScopeCommand(".scope tearoff MAP10 bl 0 7");

            Assert.Equal(".scope tearoff map10 bl 0 7", main.StatusText);
            Assert.Equal(
                new ScopeTearoff(new ScopeButtonKey(ScopeButtonId.MapToggle, "10"), new ScopePlacement(ScopeCorner.BottomLeft, 0, 7)),
                scope.Tearoffs[1]
            );
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Tearoff_PlacingATornOffKeyAgain_MovesItInItsSlot()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 1 2");
            main.RunScopeCommand(".scope tearoff brite tr 3 4");

            main.RunScopeCommand(".scope tearoff range br 5 6");

            Assert.Equal(".scope tearoff range br 5 6", main.StatusText);
            Assert.Equal(
                [Tearoff(ScopeButtonId.Range, ScopeCorner.BottomRight, 5, 6), Tearoff(ScopeButtonId.Brite, ScopeCorner.TopRight, 3, 4)],
                scope.Tearoffs
            );
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope tearoff", TearoffUsage)]
    [InlineData(".scope tearoff range tr 12", TearoffUsage)]
    [InlineData(".scope tearoff range tr 1 2 3", TearoffUsage)]
    [InlineData(".scope tearoff range xx 1 2", TearoffUsage)]
    [InlineData(".scope tearoff range tr -1 2", TearoffUsage)]
    [InlineData(".scope tearoff range tr 1 2.5", TearoffUsage)]
    [InlineData(".scope tearoff delete", TearoffUsage)]
    [InlineData(".scope tearoff NoSuch tr 1 2", "Unknown scope button: NoSuch")]
    [InlineData(".scope tearoff deletetearoff tr 1 2", "Unknown scope button: deletetearoff")]
    [InlineData(".scope tearoff map99 tr 1 2", "Unknown video map: 99")]
    [InlineData(".scope tearoff delete range Nope", "Unknown scope button: Nope")]
    [InlineData(".scope tearoff delete range db", "No tear-off: db")]
    public async Task Tearoff_Errors_SaySo_AndLeaveTheTearoffs(string command, string expected)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeWithMapsAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 1 2");
            ScopeTearoff[] before = [.. scope.Tearoffs];

            main.RunScopeCommand(command);

            Assert.Equal(expected, main.StatusText);
            Assert.Equal(before, scope.Tearoffs);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Tearoff_Delete_RemovesOnlyTheNamedKeys_AndEchoesThemCanonically()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeWithMapsAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 1 2");
            main.RunScopeCommand(".scope tearoff brite tr 3 4");
            main.RunScopeCommand(".scope tearoff map10 bl 5 6");

            main.RunScopeCommand(".scope tearoff delete MAP10 Range");

            Assert.Equal(".scope tearoff delete map10 range", main.StatusText);
            Assert.Equal([Tearoff(ScopeButtonId.Brite, ScopeCorner.TopRight, 3, 4)], scope.Tearoffs);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task TypedRange_ThroughTheCommandLine_ChangesTheRange()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.CommandText = ".scope range 30";
            await main.SendCommandCommand.ExecuteAsync(null);

            Assert.Equal(30, scope.RangeNm);
            Assert.Equal(".scope range 30", main.StatusText);

            // ".scopes" is no .scope command: it leaves the range alone and falls through to the unknown-command line.
            main.CommandText = ".scopes range 5";
            await main.SendCommandCommand.ExecuteAsync(null);
            Assert.Equal(30, scope.RangeNm);
            Assert.Equal("Unknown command or alias: .scopes", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
