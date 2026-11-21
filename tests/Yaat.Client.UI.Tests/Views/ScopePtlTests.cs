using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Entries;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's per-track PTLs: the <c>.scope ptl</c> verbs toggle, clear and refuse, echo in the status line, and the drawn
/// vector is the cue; a STARS <c>MF R</c> entry runs them for the selected track (CRC's slew); a track's PTL is
/// dropped when its aircraft leaves.
/// </summary>
public class ScopePtlTests
{
    private const string PtlUsage = "Usage: .scope ptl <callsign> | .scope ptl clear [callsign]";

    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    private static async Task<(MainViewModel Main, ScopeViewModel Scope)> StarsScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Stars;
        return (main, scope);
    }

    private static async Task RunEntryAsync(ScopeViewModel scope, string entry)
    {
        Assert.True(scope.TypeEntryText(entry));
        Assert.True(scope.ExecuteEntry());
        await scope.LastEntryRun;
    }

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeWithAViewer(string templateId)
    {
        MainViewModel main = await MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", Sector44, 3, NoStarsDisplay: true));
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot) { TemplateId = templateId };
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return (main, scope, view, window);
    }

    private static AircraftModel AddAt(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        AircraftModel ac = AddAircraft(main, callsign, 5000);
        ac.Position = new LatLon(lat, lon);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    [AvaloniaFact]
    public async Task PtlVerb_TogglesTheCallsign_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "N123AB", 5000);

            main.RunScopeCommand(".scope ptl N123AB");
            Assert.Equal(["N123AB"], scope.LocalPtlCallsigns);
            Assert.Equal("Radar PTL N123AB", main.StatusText);

            main.RunScopeCommand(".scope ptl N123AB");
            Assert.Empty(scope.LocalPtlCallsigns);
            Assert.Equal("Radar PTL N123AB removed", main.StatusText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PtlVerb_IsCaseInsensitive_AndAnswersUnknownAircraft()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "N123AB", 5000);

            main.RunScopeCommand(".scope ptl n123ab");
            Assert.Equal(["N123AB"], scope.LocalPtlCallsigns);

            main.RunScopeCommand(".scope ptl NOPE1");
            Assert.Equal("Unknown aircraft: NOPE1", main.StatusText);
            Assert.Equal(["N123AB"], scope.LocalPtlCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PtlVerb_MatchesTheCallsignExactly()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "N123AB", 5000);

            main.RunScopeCommand(".scope ptl 123AB");

            Assert.Equal("Unknown aircraft: 123AB", main.StatusText);
            Assert.Empty(scope.LocalPtlCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(".scope ptl")]
    [InlineData(".scope ptl A B")]
    [InlineData(".scope ptl clear A B")]
    public async Task PtlVerb_BadShapes_AnswerUsage(string command)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "N123AB", 5000);

            main.RunScopeCommand(command);

            Assert.Equal(PtlUsage, main.StatusText);
            Assert.Empty(scope.LocalPtlCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PtlClear_RemovesOneOrAll()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 5000);
            main.RunScopeCommand(".scope ptl UAL1");
            main.RunScopeCommand(".scope ptl UAL2");

            main.RunScopeCommand(".scope ptl clear ual1");
            Assert.Equal("Radar PTL cleared UAL1", main.StatusText);
            Assert.Equal(["UAL2"], scope.LocalPtlCallsigns);

            main.RunScopeCommand(".scope ptl clear");
            Assert.Equal("Radar PTL cleared", main.StatusText);
            Assert.Empty(scope.LocalPtlCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task PtlDraws_OnAnOtherTrack_OnTheCanvas()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeWithAViewer(ScopeTemplates.EramId);
        try
        {
            await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            // The track is not the viewer's, so the ERAM band would draw it as an LDB: pin others Full to keep its block on.
            scope.PinOthersLevel(ScopeProfile.OthersFull);
            main.RunScopeCommand(".scope vector 1");
            AircraftModel other = AddAt(main, view.Canvas, "OTH1", 300, 300);
            other.Owner = "OAK_12_CTR";
            other.OwnerFacilityId = "ZOA";
            other.OwnerSectorCode = "C12";
            Dispatcher.UIThread.RunJobs();
            Assert.Null(Assert.Single(view.Canvas.CurrentFrame.Tracks).VectorEnd);

            main.RunScopeCommand(".scope ptl OTH1");
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(Assert.Single(view.Canvas.CurrentFrame.Tracks).VectorEnd);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>CRC's MF R takes no text: a typed callsign is FORMAT, and no PTL changes.</summary>
    [AvaloniaFact]
    public async Task StarsMfR_WithATypedCallsign_AnswersFormat()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "N123AB", 5000);

            await RunEntryAsync(scope, "MF R N123AB");

            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, StarsErrorText.Format, "MF R N123AB", []), scope.EntryResponse);
            Assert.Null(scope.LastSentEntryText);
            Assert.Empty(scope.LocalPtlCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarsMfR_WithASelectedTrack_TogglesItsPtl()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AircraftModel ac = AddAircraft(main, "N123AB", 5000);
            main.SelectedAircraft = ac;

            await RunEntryAsync(scope, "MF R");

            Assert.Equal(".scope ptl N123AB", scope.LastSentEntryText);
            Assert.Equal(["N123AB"], scope.LocalPtlCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarsMfR_WithNoSelection_AnswersFormat()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            main.SelectedAircraft = null;

            await RunEntryAsync(scope, "MF R");

            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, StarsErrorText.Format, "MF R", []), scope.EntryResponse);
            Assert.Null(scope.LastSentEntryText);
            Assert.Empty(scope.LocalPtlCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RemovedAircraft_ClearsItsPtl()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 5000);
            main.RunScopeCommand(".scope ptl UAL1");
            main.RunScopeCommand(".scope ptl UAL2");

            main.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["UAL2"], scope.LocalPtlCallsigns);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
